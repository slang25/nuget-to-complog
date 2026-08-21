using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Last-resort recovery of source files that neither the PDB nor Source Link could supply, by
/// decompiling the shipped assembly.
///
/// Decompiled text never reproduces the original bytes, so a package recovered this way can
/// never rebuild byte-for-byte - the value is a source tree that *compiles*, which is what
/// patching and inspection need. That makes the file split the whole job: the PDB records which
/// document each method came from, so every type is emitted into exactly one file. Writing the
/// whole module into each missing file instead (which is what this used to do) guarantees
/// duplicate definitions and a build that cannot even start.
/// </summary>
public class SourceFileDecompilerService
{
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public SourceFileDecompilerService(IFileSystemService fileSystem, IConsoleWriter console)
    {
        _fileSystem = fileSystem;
        _console = console;
    }

    /// <summary>
    /// Decompiles the assembly into the given missing documents, one type per document as the
    /// PDB records it. Returns the number of files written.
    /// </summary>
    public async Task<int> DecompileMissingFilesAsync(
        string assemblyPath,
        IReadOnlyList<SourceFileInfo> missingSourceFiles,
        MetadataReader? pdbMetadataReader,
        string destinationDirectory,
        bool allDocumentsMissing,
        CancellationToken cancellationToken = default)
    {
        if (missingSourceFiles.Count == 0 || !File.Exists(assemblyPath))
        {
            return 0;
        }

        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(peStream);
            var peMetadata = peReader.GetMetadataReader();

            var (typesByDocument, splitTypes) = MapTypesToDocuments(
                peMetadata, pdbMetadataReader, missingSourceFiles, allDocumentsMissing);

            // The decompiler emits whatever C# expresses the IL most directly, which is newer
            // than the original build's /langversion - "ref readonly parameters" against
            // langversion 10 is CS8936 on every use. Cap it at what the original compiled with.
            var settings = new DecompilerSettings(ReadLanguageVersion(pdbMetadataReader))
            {
                ThrowOnAssemblyResolveErrors = false,
                RemoveDeadCode = false,
                DecompileMemberBodies = true,
                ShowDebugInfo = false,
            };
            var decompiler = new CSharpDecompiler(assemblyPath, settings);

            var successCount = 0;
            var emptyCount = 0;
            foreach (var sourceFile in missingSourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var types = typesByDocument.GetValueOrDefault(sourceFile.Path, []);
                    var text = types.Count > 0
                        ? decompiler.DecompileTypesAsString(types)
                        : $"// {Path.GetFileName(sourceFile.Path)}: no types in this document could be " +
                          "recovered by decompilation." + Environment.NewLine;
                    if (types.Count == 0)
                    {
                        emptyCount++;
                    }

                    var destinationPath = sourceFile.LocalPath != null
                        ? Path.Combine(destinationDirectory, sourceFile.LocalPath)
                        : GetDestinationPath(sourceFile.Path, destinationDirectory);
                    var directory = Path.GetDirectoryName(destinationPath);
                    if (directory != null && !Directory.Exists(directory))
                    {
                        _fileSystem.CreateDirectory(directory);
                    }

                    await _fileSystem.WriteAllTextAsync(destinationPath, text);
                    successCount++;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Continue with other files; the count reported back reflects what landed.
                }
            }

            if (successCount > 0)
            {
                _console.MarkupLine(
                    "  [yellow]⚠[/] Decompiled sources cannot reproduce the original bytes - " +
                    "the rebuild will compile but will not match");
                if (emptyCount > 0)
                {
                    _console.MarkupLine(
                        $"  [dim]{emptyCount} of {successCount} document(s) had no recoverable types " +
                        "(no debug info maps to them)[/]");
                }
                if (splitTypes.Count > 0)
                {
                    _console.MarkupLine(
                        $"  [yellow]⚠[/] {splitTypes.Count} type(s) are declared partially in a document " +
                        "we already have, so they were left unrecovered - the parts that lived in the " +
                        $"missing document(s) will not compile: {string.Join(", ", splitTypes.Take(5))}" +
                        (splitTypes.Count > 5 ? ", ..." : ""));
                }
            }

            return successCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The C# version recorded in the PDB's compilation-options blob, as the decompiler's
    /// nearest equivalent. Falls back to the decompiler's default when the PDB does not say.
    /// </summary>
    private static LanguageVersion ReadLanguageVersion(MetadataReader? pdbMetadata)
    {
        const string compilationOptionsGuid = "B5FEEC05-8CD0-4A83-96DA-466284BB4BD8";
        if (pdbMetadata == null)
        {
            return LanguageVersion.Latest;
        }

        try
        {
            foreach (var handle in pdbMetadata.CustomDebugInformation)
            {
                var info = pdbMetadata.GetCustomDebugInformation(handle);
                if (!pdbMetadata.GetGuid(info.Kind).ToString()
                        .Equals(compilationOptionsGuid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var options = System.Text.Encoding.UTF8
                    .GetString(pdbMetadata.GetBlobBytes(info.Value))
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i + 1 < options.Length; i += 2)
                {
                    if (options[i] == "language-version")
                    {
                        return ParseLanguageVersion(options[i + 1]);
                    }
                }
            }
        }
        catch
        {
        }

        return LanguageVersion.Latest;
    }

    private static LanguageVersion ParseLanguageVersion(string value) => value switch
    {
        "1" or "1.0" or "ISO-1" => LanguageVersion.CSharp1,
        "2" or "2.0" or "ISO-2" => LanguageVersion.CSharp2,
        "3" or "3.0" => LanguageVersion.CSharp3,
        "4" or "4.0" => LanguageVersion.CSharp4,
        "5" or "5.0" => LanguageVersion.CSharp5,
        "6" or "6.0" => LanguageVersion.CSharp6,
        "7" or "7.0" => LanguageVersion.CSharp7,
        "7.1" => LanguageVersion.CSharp7_1,
        "7.2" => LanguageVersion.CSharp7_2,
        "7.3" => LanguageVersion.CSharp7_3,
        "8" or "8.0" => LanguageVersion.CSharp8_0,
        "9" or "9.0" => LanguageVersion.CSharp9_0,
        "10" or "10.0" => LanguageVersion.CSharp10_0,
        "11" or "11.0" => LanguageVersion.CSharp11_0,
        "12" or "12.0" => LanguageVersion.CSharp12_0,
        _ => LanguageVersion.Latest,
    };

    /// <summary>
    /// Assigns every top-level type to the single document that owns it, using the PDB's
    /// per-method document records. A partial class has methods in several documents, so the
    /// document holding most of them wins - emitting the type into each would redefine it.
    /// Types the PDB says belong to a document we already have are left out entirely, since
    /// that file will define them.
    ///
    /// Also returns the types that had to be skipped because they span a recovered document
    /// and a missing one, so the caller can say so.
    /// </summary>
    private static (Dictionary<string, List<TypeDefinitionHandle>> ByDocument, List<string> SplitTypes)
        MapTypesToDocuments(
            MetadataReader peMetadata,
            MetadataReader? pdbMetadata,
            IReadOnlyList<SourceFileInfo> missingSourceFiles,
            bool allDocumentsMissing)
    {
        var result = new Dictionary<string, List<TypeDefinitionHandle>>(StringComparer.OrdinalIgnoreCase);
        var splitTypes = new List<string>();
        if (pdbMetadata == null)
        {
            return (result, splitTypes);
        }

        var missing = missingSourceFiles.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var votes = new Dictionary<TypeDefinitionHandle, Dictionary<string, int>>();

        foreach (var handle in pdbMetadata.MethodDebugInformation)
        {
            var debugInfo = pdbMetadata.GetMethodDebugInformation(handle);
            var documentHandle = debugInfo.Document;
            if (documentHandle.IsNil)
            {
                // A method spanning documents records them per sequence point instead.
                foreach (var sequencePoint in debugInfo.GetSequencePoints())
                {
                    documentHandle = sequencePoint.Document;
                    break;
                }
            }
            if (documentHandle.IsNil)
            {
                continue;
            }

            var documentName = pdbMetadata.GetString(pdbMetadata.GetDocument(documentHandle).Name);

            TypeDefinitionHandle declaringType;
            try
            {
                declaringType = peMetadata.GetMethodDefinition(handle.ToDefinitionHandle()).GetDeclaringType();
            }
            catch
            {
                continue;
            }

            var topLevel = GetTopLevelType(peMetadata, declaringType);
            if (topLevel.IsNil)
            {
                continue;
            }

            if (!votes.TryGetValue(topLevel, out var perDocument))
            {
                votes[topLevel] = perDocument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            perDocument[documentName] = perDocument.GetValueOrDefault(documentName) + 1;
        }

        foreach (var (type, perDocument) in votes)
        {
            var owner = perDocument.OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                .First().Key;
            // A partial type split across a recovered document and a missing one is already
            // declared by the file we have. The decompiler only emits whole types, so writing
            // it here would repeat every member the recovered part declares (CS0111) and
            // redeclare the type without `partial` (CS0260) - the rebuild would not even
            // parse. Leaving it out costs the members that lived in the missing file, which
            // the caller reports - and it costs them whichever side won the vote, so the split
            // is detected before the winner is looked at.
            if (perDocument.Keys.Any(missing.Contains) &&
                perDocument.Keys.Any(document => !missing.Contains(document)))
            {
                splitTypes.Add(GetTypeName(peMetadata, type));
                continue;
            }
            if (!missing.Contains(owner))
            {
                continue;
            }
            if (!result.TryGetValue(owner, out var list))
            {
                result[owner] = list = [];
            }
            list.Add(type);
        }

        // Types with no debug info at all (interfaces, enums, delegates) can only be placed when
        // every document is missing. If any document was recovered, one of them most likely
        // declares the type already, and emitting it here would define it twice.
        if (allDocumentsMissing)
        {
            // Normally the document that already owns the most types takes them. An assembly
            // whose source declares no method bodies at all (enums, interfaces) has no method
            // debug info, so nothing established an owner - fall back to a missing document,
            // otherwise every file would be written as a placeholder comment.
            var catchAll = result.Count > 0
                ? result.OrderByDescending(kvp => kvp.Value.Count)
                    .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .First().Key
                : missingSourceFiles.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (catchAll != null)
            {
                if (!result.TryGetValue(catchAll, out var catchAllTypes))
                {
                    result[catchAll] = catchAllTypes = [];
                }

                var placed = votes.Keys.ToHashSet();
                foreach (var handle in peMetadata.TypeDefinitions)
                {
                    var type = peMetadata.GetTypeDefinition(handle);
                    if (type.IsNested || placed.Contains(handle))
                    {
                        continue;
                    }
                    // <Module> carries no source-level declaration.
                    if (peMetadata.GetString(type.Name) == "<Module>")
                    {
                        continue;
                    }
                    catchAllTypes.Add(handle);
                }
            }
        }

        return (result, splitTypes);
    }

    private static string GetTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static TypeDefinitionHandle GetTopLevelType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        // Nested types are emitted with their declaring type, so vote and decompile at the top.
        for (var depth = 0; depth < 32 && !handle.IsNil; depth++)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.IsNested)
            {
                return handle;
            }
            handle = type.GetDeclaringType();
        }
        return default;
    }

    private string GetDestinationPath(string sourceFilePath, string destinationDirectory)
    {
        var normalizedPath = sourceFilePath.Replace('\\', '/').TrimStart('/');

        // Strip common prefixes
        var patterns = new[] { "_/Src/", "_/src/", "Src/", "src/" };
        foreach (var pattern in patterns)
        {
            var idx = normalizedPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                normalizedPath = normalizedPath.Substring(idx + pattern.Length);
                break;
            }
        }

        // Skip first directory component (package name)
        var parts = normalizedPath.Split('/');
        if (parts.Length > 1)
        {
            normalizedPath = string.Join("/", parts.Skip(1));
        }

        return Path.Combine(destinationDirectory, normalizedPath);
    }
}
