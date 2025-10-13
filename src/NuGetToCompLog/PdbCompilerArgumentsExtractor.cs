using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO.Compression;
using System.Text.Json;
using Spectre.Console;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGet.Frameworks;
using NuGet.Packaging;

namespace NuGetToCompLog;

/// <summary>
/// Extracts compiler arguments and compilation metadata from portable PDB files.
/// Supports both embedded PDBs (in assemblies) and external PDB files (.pdb or from .snupkg).
/// 
/// The extraction process reads custom debug information from the PDB that contains:
/// - Compiler command-line arguments (options, defines, language version, etc.)
/// - Metadata references (all assemblies referenced during compilation)
/// - Source file listings (original source file paths)
/// - Source Link mappings (URLs to source code in version control)
/// - Embedded source code (when available)
/// 
/// This information is essential for reconstructing a Roslyn compilation workspace
/// and creating a portable CompLog file.
/// </summary>
public class PdbCompilerArgumentsExtractor
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly HashSet<string> _downloadedPackages = [];
    private readonly HashSet<string> _downloadedFrameworkPacks = [];
    private string _workingDirectory = "";
    private List<string> _compilerArguments = [];
    private List<string> _metadataReferences = [];
    
    public async Task ExtractCompilerArgumentsAsync(string assemblyPath, string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        _compilerArguments = [];
        _metadataReferences = [];
        
        // Try three approaches to find the PDB:
        // 1. Embedded PDB in the assembly
        // 2. External PDB file next to the assembly
        // 3. External PDB in symbols package (download if necessary)

        string? pdbPath = null;
        string? pdbFileName = null;

        // Check for embedded PDB
        await using (var peStream = File.OpenRead(assemblyPath))
        using (var peReader = new PEReader(peStream))
        {
            var embeddedPdb = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            if (embeddedPdb.DataSize > 0)
            {
                AnsiConsole.MarkupLine("  [green]✓ Found embedded PDB[/]");
                await ExtractFromEmbeddedPdbAsync(peReader);
                return;
            }

            // Check for CodeView entry which points to external PDB
            var codeView = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.CodeView);

            if (codeView.DataSize > 0)
            {
                var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeView);
                pdbFileName = codeViewData.Path;
                AnsiConsole.MarkupLine($"  [dim]→ PDB reference: {pdbFileName}[/]");

                // Try to find the PDB file
                pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
                
                if (pdbPath != null)
                {
                    // Show which PDB was found and its path to verify TFM matching
                    var workingDirPath = Path.Combine(workingDirectory, "symbols");
                    if (pdbPath.StartsWith(workingDirPath))
                    {
                        var relativePdbPath = Path.GetRelativePath(workingDirPath, pdbPath);
                        AnsiConsole.MarkupLine($"  [green]✓[/] Found PDB in symbols: [dim]{relativePdbPath}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"  [green]✓[/] Found external PDB: [dim]{Path.GetFileName(pdbPath)}[/]");
                    }
                }
            }
        }

        // If we still don't have a PDB, try downloading the symbols package
        if (pdbPath == null && pdbFileName != null)
        {
            var symbolsDir = Path.Combine(workingDirectory, "symbols");
            
            // Check if symbols directory already exists (might have been downloaded by orchestrator)
            if (!Directory.Exists(symbolsDir) || Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories).Length == 0)
            {
                AnsiConsole.MarkupLine("  [dim]→ Attempting to download symbols package...[/]");
                var snupkgDownloaded = await TryDownloadSymbolsPackageAsync(assemblyPath, workingDirectory);
                
                if (snupkgDownloaded)
                {
                    // Try to find the PDB again after downloading symbols
                    pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
                    
                    if (pdbPath != null)
                    {
                        // Show which PDB was found and its path to verify TFM matching
                        if (pdbPath.StartsWith(symbolsDir))
                        {
                            var relativePdbPath = Path.GetRelativePath(symbolsDir, pdbPath);
                            AnsiConsole.MarkupLine($"  [green]✓[/] Found PDB in symbols: [dim]{relativePdbPath}[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"  [green]✓[/] Found external PDB: [dim]{Path.GetFileName(pdbPath)}[/]");
                        }
                    }
                }
            }
        }

        if (pdbPath != null && File.Exists(pdbPath))
        {
            AnsiConsole.MarkupLine($"  [green]✓ Found external PDB:[/] [cyan]{Path.GetFileName(pdbPath)}[/]");
            await ExtractFromExternalPdbAsync(pdbPath);
        }
        else
        {
            var noSymbolsPanel = new Panel(
                "[yellow]No PDB found[/] - cannot extract compiler arguments\n\n" +
                "[dim]Note: Reproducible builds with embedded symbols are required for complog extraction[/]")
                .BorderColor(Color.Yellow)
                .Header("[yellow]⚠ Missing Symbols[/]");
            AnsiConsole.Write(noSymbolsPanel);
        }
    }

    private async Task ExtractFromEmbeddedPdbAsync(PEReader peReader)
    {
        var embeddedPdb = peReader.ReadDebugDirectory()
            .First(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
        var metadataReader = pdbProvider.GetMetadataReader();

        await ExtractCompilationOptionsAsync(metadataReader);
    }

    private async Task ExtractFromExternalPdbAsync(string pdbPath)
    {
        await using var pdbStream = File.OpenRead(pdbPath);
        using var metadataReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var metadataReader = metadataReaderProvider.GetMetadataReader();

        await ExtractCompilationOptionsAsync(metadataReader);
    }

    private async Task ExtractCompilationOptionsAsync(MetadataReader metadataReader)
    {
        AnsiConsole.WriteLine();
        
        var compilationPanel = new Panel("")
            .Header("[cyan]Compilation Information[/]")
            .BorderColor(Color.Cyan1)
            .Expand();

        // Extract custom debug information (this is where compiler args live)
        foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
            var guid = metadataReader.GetGuid(cdi.Kind);

            // CompilationOptions GUID: {B5FEEC05-8CD0-4A83-96DA-466284BB4BD8}
            if (guid.ToString().Equals("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8", StringComparison.OrdinalIgnoreCase))
            {
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                var options = System.Text.Encoding.UTF8.GetString(blob);
                
                var argsTable = new Table()
                    .BorderColor(Color.Grey)
                    .AddColumn("[yellow]Compiler Arguments[/]");
                
                var args = ParseCompilerArguments(options);
                _compilerArguments.AddRange(args); // Store for later use
                foreach (var arg in args)
                {
                    argsTable.AddRow($"[cyan]{arg}[/]");
                }
                AnsiConsole.Write(argsTable);
                AnsiConsole.WriteLine();
                
                // Save compiler arguments to file
                var compilerArgsPath = Path.Combine(_workingDirectory, "compiler-arguments.txt");
                await File.WriteAllLinesAsync(compilerArgsPath, args);
            }

            // CompilationMetadataReferences GUID: {7E4D4708-096E-4C5C-AEDA-CB10BA6A740D}
            if (guid.ToString().Equals("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D", StringComparison.OrdinalIgnoreCase))
            {
                var blobReader = metadataReader.GetBlobReader(cdi.Value);
                AnsiConsole.MarkupLine("[yellow]Metadata References:[/]");
                await ParseMetadataReferencesAsync(blobReader);
                AnsiConsole.WriteLine();
            }
        }

        // Extract source files and Source Link information
        var sourceFilesTree = new Tree("[yellow]Source Files[/]");
        
        int documentCount = 0;
        var sourceLinkUrls = new Dictionary<string, string>();
        var documentsToDownload = new List<(DocumentHandle handle, string path)>();

        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);
            var language = metadataReader.GetGuid(document.Language);
            
            documentCount++;
            
            // Check for embedded source
            var embeddedSource = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .Where(
                    cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE",
                        StringComparison.OrdinalIgnoreCase))
                .Cast<CustomDebugInformation?>()
                .FirstOrDefault();

            if (embeddedSource.HasValue && !(embeddedSource.Value.Kind != default))
            {
                sourceFilesTree.AddNode($"[green]{name}[/] [dim](embedded)[/]");
            }
            else
            {
                sourceFilesTree.AddNode($"[cyan]{name}[/]");
                documentsToDownload.Add((docHandle, name));
            }
        }

        AnsiConsole.Write(sourceFilesTree);
        AnsiConsole.MarkupLine($"  [dim]Total: {documentCount} source files[/]");
        AnsiConsole.WriteLine();

        // Extract Source Link information
        var sourceLinkHandle = metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
            .Select(h => metadataReader.GetCustomDebugInformation(h))
            .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("CC110556-A091-4D38-9FEC-25AB9A351A6A", StringComparison.OrdinalIgnoreCase));

        string? sourceLinkJson = null;
        if (sourceLinkHandle.Kind != default)
        {
            var blob = metadataReader.GetBlobBytes(sourceLinkHandle.Value);
            sourceLinkJson = System.Text.Encoding.UTF8.GetString(blob);
            
            var sourceLinkPanel = new Panel(new Markup($"[dim]{sourceLinkJson}[/]"))
                .Header("[cyan]Source Link Configuration[/]")
                .BorderColor(Color.Cyan1);
            AnsiConsole.Write(sourceLinkPanel);
            AnsiConsole.WriteLine();
        }

        // Download all source files
        await DownloadSourceFilesAsync(metadataReader, documentsToDownload, sourceLinkJson);

        await Task.CompletedTask;
    }

    private async Task DownloadSourceFilesAsync(
        MetadataReader metadataReader,
        List<(DocumentHandle handle, string path)> documentsToDownload,
        string? sourceLinkJson)
    {
        var sourcesDir = Path.Combine(_workingDirectory, "sources");
        Directory.CreateDirectory(sourcesDir);

        AnsiConsole.MarkupLine("[yellow]Downloading Source Files:[/]");
        
        int embeddedCount = 0;
        int downloadedCount = 0;
        int failedCount = 0;

        // Parse Source Link mappings
        var sourceLinkMappings = ParseSourceLinkMappings(sourceLinkJson);

        // First, extract all embedded sources
        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);

            var embeddedSourceCdi = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .Where(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE",
                    StringComparison.OrdinalIgnoreCase))
                .Cast<CustomDebugInformation?>()
                .FirstOrDefault();

            if (embeddedSourceCdi.HasValue && embeddedSourceCdi.Value.Kind != default)
            {
                try
                {
                    var embeddedSourceBlob = metadataReader.GetBlobBytes(embeddedSourceCdi.Value.Value);
                    var sourceText = DecompressEmbeddedSource(embeddedSourceBlob);
                    
                    if (sourceText != null)
                    {
                        var localPath = SaveSourceFile(sourcesDir, name, sourceText);
                        embeddedCount++;
                        
                        if (embeddedCount <= 3) // Show first few
                        {
                            AnsiConsole.MarkupLine($"  [green]✓[/] Extracted embedded: [dim]{Path.GetFileName(name)}[/]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/] Failed to extract embedded source [dim]{Path.GetFileName(name)}: {ex.Message}[/]");
                    failedCount++;
                }
            }
        }

        if (embeddedCount > 3)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] Extracted {embeddedCount} embedded source files");
        }

        // Download sources from Source Link URLs
        if (sourceLinkMappings.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [cyan]Downloading from Source Link URLs...[/]");
            
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "NuGetToCompLog/1.0");

            var downloadTasks = new List<Task<(bool success, string fileName)>>();
            var semaphore = new SemaphoreSlim(5); // Limit concurrent downloads

            foreach (var (handle, path) in documentsToDownload)
            {
                // Skip if already extracted as embedded
                var document = metadataReader.GetDocument(handle);
                var hasEmbedded = metadataReader.GetCustomDebugInformation(handle)
                    .Select(h => metadataReader.GetCustomDebugInformation(h))
                    .Any(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE",
                        StringComparison.OrdinalIgnoreCase));

                if (hasEmbedded) continue;

                var url = MapSourceLinkUrl(path, sourceLinkMappings);
                if (url == null) continue;

                downloadTasks.Add(DownloadSourceFileAsync(httpClient, semaphore, url, path, sourcesDir));
            }

            var results = await Task.WhenAll(downloadTasks);
            downloadedCount = results.Count(r => r.success);
            failedCount += results.Count(r => !r.success);

            if (downloadedCount > 0)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] Downloaded {downloadedCount} source files from repository");
            }
        }

        AnsiConsole.WriteLine();
        var summary = new Panel(
            $"[green]✓ Embedded sources:[/] {embeddedCount}\n" +
            $"[cyan]✓ Downloaded from URLs:[/] {downloadedCount}\n" +
            (failedCount > 0 ? $"[yellow]⚠ Failed:[/] {failedCount}\n" : "") +
            $"[dim]Total source files:[/] {embeddedCount + downloadedCount}")
            .Header("[yellow]Source Download Summary[/]")
            .BorderColor(Color.Yellow);
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();
    }

    private Dictionary<string, string> ParseSourceLinkMappings(string? sourceLinkJson)
    {
        var mappings = new Dictionary<string, string>();
        
        if (string.IsNullOrEmpty(sourceLinkJson))
        {
            return mappings;
        }

        try
        {
            using var doc = JsonDocument.Parse(sourceLinkJson);
            if (doc.RootElement.TryGetProperty("documents", out var documents))
            {
                foreach (var prop in documents.EnumerateObject())
                {
                    mappings[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] Failed to parse Source Link JSON: [dim]{ex.Message}[/]");
        }

        return mappings;
    }

    private string? MapSourceLinkUrl(string localPath, Dictionary<string, string> mappings)
    {
        // Source Link mappings use wildcards like:
        // "C:\path\*" -> "https://raw.githubusercontent.com/user/repo/commit/*"
        
        var normalizedPath = localPath.Replace('\\', '/');
        
        foreach (var (pattern, urlTemplate) in mappings)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            
            // Check if pattern contains wildcard
            var wildcardIndex = normalizedPattern.IndexOf('*');
            if (wildcardIndex < 0) continue;

            var prefix = normalizedPattern.Substring(0, wildcardIndex);
            
            // Check if the path matches the prefix
            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = normalizedPath.Substring(prefix.Length);
                var url = urlTemplate.Replace("*", relativePath);
                return url;
            }
        }

        return null;
    }

    private async Task<(bool success, string fileName)> DownloadSourceFileAsync(
        HttpClient httpClient,
        SemaphoreSlim semaphore,
        string url,
        string localPath,
        string sourcesDir)
    {
        await semaphore.WaitAsync();
        
        try
        {
            var response = await httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var savedPath = SaveSourceFile(sourcesDir, localPath, content);
                
                return (true, Path.GetFileName(localPath));
            }
            else
            {
                return (false, Path.GetFileName(localPath));
            }
        }
        catch
        {
            return (false, Path.GetFileName(localPath));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private string? DecompressEmbeddedSource(byte[] blob)
    {
        // Embedded source format:
        // First 4 bytes: uncompressed size (int32)
        // Remaining bytes: deflate-compressed UTF-8 text
        
        if (blob.Length < 4)
        {
            return null;
        }

        try
        {
            var uncompressedSize = BitConverter.ToInt32(blob, 0);
            
            // If uncompressed size is 0, the source is stored uncompressed
            if (uncompressedSize == 0)
            {
                return System.Text.Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
            }

            using var compressedStream = new MemoryStream(blob, 4, blob.Length - 4);
            using var deflateStream = new System.IO.Compression.DeflateStream(compressedStream, CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream(uncompressedSize);
            
            deflateStream.CopyTo(decompressedStream);
            
            return System.Text.Encoding.UTF8.GetString(decompressedStream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private string SaveSourceFile(string sourcesDir, string originalPath, string content)
    {
        // Save the source file with a flattened structure
        // We want files to end up directly under /src/ in the complog export
        // So we strip any leading path components and just keep the file structure
        
        // Normalize path separators
        var normalizedPath = originalPath.Replace('\\', '/');
        
        // Remove leading slash if present
        normalizedPath = normalizedPath.TrimStart('/');
        
        // Strip common prefixes like "_/Src/" or "_/src/" or just preserve after last known root
        // Patterns we want to remove: _/Src/, _/src/, Src/, src/
        var patterns = new[] { "_/Src/", "_/src/", "Src/", "src/" };
        foreach (var pattern in patterns)
        {
            var idx = normalizedPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // Take everything after the pattern, including package subdirectory
                normalizedPath = normalizedPath.Substring(idx + pattern.Length);
                break;
            }
        }
        
        // Now normalizedPath might be like "Newtonsoft.Json/JsonConvert.cs"
        // We want to strip the first directory component to get just "JsonConvert.cs"
        // or preserve subfolders within the library
        var parts = normalizedPath.Split('/');
        if (parts.Length > 1)
        {
            // Skip the first part (package name) and keep the rest
            normalizedPath = string.Join("/", parts.Skip(1));
        }
        
        // Create the full output path within sources directory
        var fullPath = Path.Combine(sourcesDir, normalizedPath);
        var directory = Path.GetDirectoryName(fullPath);
        
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private string[] ParseCompilerArguments(string options)
    {
        // Compiler arguments are stored as null-terminated strings
        return options.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task ParseMetadataReferencesAsync(BlobReader blobReader)
    {
        try
        {
            var references = MetadataReferenceParser.Parse(blobReader);
            
            // Store references for later use
            _metadataReferences.AddRange(references.Select(r => r.FileName));
            
            var refsTable = new Table()
                .BorderColor(Color.Grey)
                .AddColumn("[dim]#[/]")
                .AddColumn("[cyan]Reference[/]")
                .AddColumn("[yellow]Aliases[/]")
                .AddColumn("[dim]Kind[/]")
                .AddColumn("[dim]Properties[/]");

            int displayCount = Math.Min(references.Count, 50);
            for (int i = 0; i < displayCount; i++)
            {
                var reference = references[i];
                refsTable.AddRow(
                    $"[dim]{i + 1}[/]",
                    $"[cyan]{Path.GetFileName(reference.FileName)}[/]",
                    reference.ExternAliases.Count > 0 ? string.Join(", ", reference.ExternAliases) : "[dim]-[/]",
                    $"[dim]{reference.Kind}[/]",
                    $"[dim]Embed: {reference.EmbedInteropTypes}[/]"
                );
            }

            AnsiConsole.Write(refsTable);
            if (references.Count > 50)
            {
                AnsiConsole.MarkupLine($"[dim]... and {references.Count - 50} more references[/]");
            }
            AnsiConsole.MarkupLine($"  [dim]Total: {references.Count} references[/]");
            
            // Save metadata references to file
            var referencesPath = Path.Combine(_workingDirectory, "metadata-references.txt");
            await File.WriteAllLinesAsync(referencesPath, _metadataReferences);

            // Acquire all reference assemblies and download dependent NuGet packages
            await AcquireReferenceAssembliesAsync(references, _workingDirectory);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not fully parse metadata references: [dim]{ex.Message}[/]");
        }
    }

    private async Task AcquireReferenceAssembliesAsync(List<MetadataReference> references, string workingDirectory)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]Acquiring Reference Assemblies:[/]");
        
        var referencesDir = Path.Combine(workingDirectory, "references");
        Directory.CreateDirectory(referencesDir);

        // Determine the target framework from the compiler arguments or references
        var targetFramework = DetermineTargetFramework(references);
        AnsiConsole.MarkupLine($"  [dim]→ Target framework: {targetFramework ?? "unknown"}[/]");

        var frameworkRefs = new List<MetadataReference>();
        var nugetRefs = new List<MetadataReference>();
        
        // Categorize references
        foreach (var reference in references)
        {
            if (IsFrameworkAssembly(reference.FileName))
            {
                frameworkRefs.Add(reference);
            }
            else if (IsNuGetPackageReference(reference.FileName))
            {
                nugetRefs.Add(reference);
            }
        }

        AnsiConsole.MarkupLine($"  [cyan]Framework assemblies:[/] {frameworkRefs.Count}");
        AnsiConsole.MarkupLine($"  [cyan]NuGet package references:[/] {nugetRefs.Count}");
        AnsiConsole.WriteLine();

        // Download framework reference assemblies
        if (frameworkRefs.Count > 0 && targetFramework != null)
        {
            await DownloadFrameworkReferencePackAsync(targetFramework, referencesDir);
        }

        // Download NuGet package dependencies recursively
        foreach (var nugetRef in nugetRefs)
        {
            var packageInfo = ExtractPackageInfoFromPath(nugetRef.FileName);
            if (packageInfo.HasValue)
            {
                await DownloadNuGetPackageRecursivelyAsync(
                    packageInfo.Value.PackageId, 
                    packageInfo.Value.Version, 
                    referencesDir,
                    targetFramework);
            }
        }
    }

    private string? DetermineTargetFramework(List<MetadataReference> references)
    {
        // Try to determine target framework from reference paths
        // NuGet package references typically have paths like:
        // .nuget/packages/{package}/{version}/lib/{tfm}/{assembly}.dll
        // or ref/{tfm}/{assembly}.dll
        
        foreach (var reference in references)
        {
            var path = reference.FileName.Replace('\\', '/');
            
            // Look for common TFM patterns in paths
            var tfmPatterns = new[] { "/lib/", "/ref/" };
            foreach (var pattern in tfmPatterns)
            {
                var index = path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var afterPattern = path.Substring(index + pattern.Length);
                    var nextSlash = afterPattern.IndexOf('/');
                    if (nextSlash > 0)
                    {
                        var tfm = afterPattern.Substring(0, nextSlash);
                        // Validate it looks like a TFM (e.g., net8.0, netstandard2.0, net6.0)
                        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && 
                            (tfm.Contains(".") || char.IsDigit(tfm[3])))
                        {
                            return tfm;
                        }
                    }
                }
            }
        }
        
        return null;
    }

    private bool IsFrameworkAssembly(string fileName)
    {
        // Framework assemblies typically:
        // 1. Are in the dotnet/packs directory
        // 2. Have common BCL names (System.*, Microsoft.CSharp, mscorlib, etc.)
        // 3. Are in the Microsoft.NETCore.App.Ref or similar reference packs
        
        var path = fileName.Replace('\\', '/').ToLowerInvariant();
        
        // Check for dotnet packs/shared directory
        if (path.Contains("/packs/") || path.Contains("/shared/"))
        {
            return true;
        }
        
        // Check for well-known framework assembly names
        var assemblyName = Path.GetFileNameWithoutExtension(fileName);
        var frameworkAssemblyPrefixes = new[]
        {
            "System.", "Microsoft.CSharp", "Microsoft.VisualBasic", "Microsoft.Win32.",
            "netstandard", "mscorlib", "WindowsBase", "PresentationCore", "PresentationFramework"
        };
        
        return frameworkAssemblyPrefixes.Any(prefix => 
            assemblyName.Equals(prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsNuGetPackageReference(string fileName)
    {
        // NuGet package references typically have paths like:
        // C:\Users\user\.nuget\packages\{package}\{version}\lib\{tfm}\{assembly}.dll
        // or similar patterns
        
        var path = fileName.Replace('\\', '/').ToLowerInvariant();
        return path.Contains("/.nuget/packages/") || path.Contains("\\.nuget\\packages\\");
    }

    private (string PackageId, string Version)? ExtractPackageInfoFromPath(string fileName)
    {
        // Extract package ID and version from NuGet package path
        // Typical path: .nuget/packages/{packageId}/{version}/lib/{tfm}/{assembly}.dll
        
        var path = fileName.Replace('\\', '/');
        var packagesIndex = path.IndexOf("/.nuget/packages/", StringComparison.OrdinalIgnoreCase);
        
        if (packagesIndex >= 0)
        {
            var afterPackages = path[(packagesIndex + "/.nuget/packages/".Length)..];
            var parts = afterPackages.Split('/');
            
            if (parts.Length >= 2)
            {
                var packageId = parts[0];
                var version = parts[1];
                return (packageId, version);
            }
        }
        
        return null;
    }

    private async Task DownloadFrameworkReferencePackAsync(string targetFramework, string referencesDir)
    {
        // Framework reference packs are distributed as NuGet packages
        // For example: Microsoft.NETCore.App.Ref for .NET Core/5+
        
        var frameworkKey = $"{targetFramework}";
        if (_downloadedFrameworkPacks.Contains(frameworkKey))
        {
            return; // Already downloaded
        }

        AnsiConsole.MarkupLine($"  [yellow]→[/] Downloading framework reference pack for [cyan]{targetFramework}[/]...");
        
        try
        {
            // Map TFM to reference pack package
            var refPackage = GetReferencePackageForFramework(targetFramework);
            if (refPackage == null)
            {
                AnsiConsole.MarkupLine($"    [dim]No reference pack mapping found for {targetFramework}[/]");
                return;
            }

            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            // Get all versions and find one matching or close to the TFM version
            var versions = await resource.GetAllVersionsAsync(
                refPackage,
                cache,
                _logger,
                CancellationToken.None);

            var targetVersion = ExtractVersionFromTfm(targetFramework);
            var version = FindBestMatchingVersion(versions, targetVersion);
            
            if (version == null)
            {
                AnsiConsole.MarkupLine($"    [dim]No suitable version found for {refPackage}[/]");
                return;
            }

            var packagePath = Path.Combine(referencesDir, $"{refPackage}.{version}.nupkg");
            
            // Check if already exists
            if (File.Exists(packagePath))
            {
                AnsiConsole.MarkupLine($"    [green]✓[/] Reference pack already downloaded");
                _downloadedFrameworkPacks.Add(frameworkKey);
                return;
            }

            await using var packageStream = File.Create(packagePath);
            var success = await resource.CopyNupkgToStreamAsync(
                refPackage,
                version,
                packageStream,
                cache,
                _logger,
                CancellationToken.None);

            if (success)
            {
                packageStream.Close();
                
                // Extract the package to get the reference assemblies
                var extractPath = Path.Combine(referencesDir, $"{refPackage}.{version}");
                if (!Directory.Exists(extractPath))
                {
                    await ZipFile.ExtractToDirectoryAsync(packagePath, extractPath);
                }
                
                AnsiConsole.MarkupLine($"    [green]✓[/] Downloaded {refPackage} {version}");
                _downloadedFrameworkPacks.Add(frameworkKey);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"    [yellow]⚠[/] Failed to download framework reference pack: [dim]{ex.Message}[/]");
        }
    }

    private string? GetReferencePackageForFramework(string targetFramework)
    {
        // Map TFMs to their reference pack packages
        var tfmLower = targetFramework.ToLowerInvariant();
        
        if (tfmLower.StartsWith("net") && !tfmLower.StartsWith("netstandard") && !tfmLower.StartsWith("netcoreapp"))
        {
            // .NET 5+ uses Microsoft.NETCore.App.Ref
            if (tfmLower.StartsWith("net5") || tfmLower.StartsWith("net6") || 
                tfmLower.StartsWith("net7") || tfmLower.StartsWith("net8") ||
                tfmLower.StartsWith("net9") || tfmLower.StartsWith("net10"))
            {
                return "Microsoft.NETCore.App.Ref";
            }
        }
        
        if (tfmLower.StartsWith("netcoreapp"))
        {
            return "Microsoft.NETCore.App.Ref";
        }
        
        if (tfmLower.StartsWith("netstandard"))
        {
            return "NETStandard.Library.Ref";
        }
        
        // .NET Framework doesn't have NuGet reference packs
        return null;
    }

    private string? ExtractVersionFromTfm(string targetFramework)
    {
        // Extract version number from TFM
        // e.g., net8.0 -> 8.0, netcoreapp3.1 -> 3.1
        
        var tfm = targetFramework.ToLowerInvariant();
        
        if (tfm.StartsWith("net"))
        {
            var versionPart = tfm.Substring(3);
            
            // Remove "standard", "coreapp", etc.
            versionPart = versionPart.Replace("standard", "").Replace("coreapp", "");
            
            // Parse the version
            if (versionPart.Contains("."))
            {
                return versionPart;
            }
            else if (versionPart.Length > 0)
            {
                // Handle cases like net6 -> 6.0
                if (int.TryParse(versionPart, out var major))
                {
                    return $"{major}.0";
                }
            }
        }
        
        return null;
    }

    private NuGetVersion? FindBestMatchingVersion(IEnumerable<NuGetVersion> versions, string? targetVersion)
    {
        var versionList = versions.Where(v => !v.IsPrerelease).OrderByDescending(v => v).ToList();
        
        if (versionList.Count == 0)
        {
            return null;
        }
        
        if (string.IsNullOrEmpty(targetVersion))
        {
            return versionList.First();
        }

        // Try to find a version matching the target
        foreach (var version in versionList)
        {
            if (version.ToString().StartsWith(targetVersion))
            {
                return version;
            }
        }
        
        // Return latest
        return versionList.First();
    }

    private async Task DownloadNuGetPackageRecursivelyAsync(
        string packageId, 
        string versionString, 
        string referencesDir,
        string? targetFramework)
    {
        var packageKey = $"{packageId}/{versionString}";
        if (_downloadedPackages.Contains(packageKey))
        {
            return; // Already downloaded
        }

        AnsiConsole.MarkupLine($"  [yellow]→[/] Downloading NuGet package [cyan]{packageId}[/] [dim]{versionString}[/]...");
        
        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            var version = NuGetVersion.Parse(versionString);
            var packagePath = Path.Combine(referencesDir, $"{packageId}.{version}.nupkg");
            
            // Check if already exists
            if (File.Exists(packagePath))
            {
                AnsiConsole.MarkupLine($"    [green]✓[/] Package already downloaded");
                _downloadedPackages.Add(packageKey);
                
                // Still need to process dependencies
                await ProcessPackageDependenciesAsync(packagePath, referencesDir, targetFramework);
                return;
            }

            await using var packageStream = File.Create(packagePath);
            var success = await resource.CopyNupkgToStreamAsync(
                packageId,
                version,
                packageStream,
                cache,
                _logger,
                CancellationToken.None);

            if (success)
            {
                packageStream.Close();
                AnsiConsole.MarkupLine($"    [green]✓[/] Downloaded {packageId} {version}");
                _downloadedPackages.Add(packageKey);
                
                // Extract package to get assemblies
                var extractPath = Path.Combine(referencesDir, $"{packageId}.{version}");
                if (!Directory.Exists(extractPath))
                {
                    ZipFile.ExtractToDirectory(packagePath, extractPath);
                }
                
                // Recursively process dependencies
                await ProcessPackageDependenciesAsync(packagePath, referencesDir, targetFramework);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"    [yellow]⚠[/] Failed to download {packageId}: [dim]{ex.Message}[/]");
        }
    }

    private async Task ProcessPackageDependenciesAsync(string packagePath, string referencesDir, string? targetFramework)
    {
        try
        {
            using var packageReader = new PackageArchiveReader(packagePath);
            var nuspecReader = await packageReader.GetNuspecReaderAsync(CancellationToken.None);
            
            // Get dependency groups
            var dependencyGroups = nuspecReader.GetDependencyGroups();
            
            // Find the best matching dependency group for the target framework
            NuGetFramework? targetNuGetFramework = null;
            if (targetFramework != null)
            {
                targetNuGetFramework = NuGetFramework.Parse(targetFramework);
            }
            
            foreach (var depGroup in dependencyGroups)
            {
                // If we have a target framework, only process matching dependencies
                if (targetNuGetFramework != null)
                {
                    // Use a simple compatibility check
                    if (!IsCompatibleFramework(depGroup.TargetFramework, targetNuGetFramework))
                    {
                        continue;
                    }
                }
                
                foreach (var dependency in depGroup.Packages)
                {
                    // Resolve the version range to a specific version
                    var specificVersion = await ResolveVersionRangeAsync(dependency.Id, dependency.VersionRange);
                    if (specificVersion != null)
                    {
                        await DownloadNuGetPackageRecursivelyAsync(
                            dependency.Id,
                            specificVersion,
                            referencesDir,
                            targetFramework);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"    [dim]Note: Could not process dependencies: {ex.Message}[/]");
        }
    }

    private bool IsCompatibleFramework(NuGetFramework packageFramework, NuGetFramework targetFramework)
    {
        // Simple compatibility check
        // A more robust solution would use NuGet's FrameworkReducer
        
        // If package is netstandard, it's compatible with most frameworks
        if (packageFramework.Framework.Equals("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        
        // If package is .NETCoreApp and target is .NET 5+, check version compatibility
        if (packageFramework.Framework.Equals(targetFramework.Framework, StringComparison.OrdinalIgnoreCase))
        {
            return packageFramework.Version <= targetFramework.Version;
        }
        
        // If no specific target, accept all
        if (packageFramework.IsAny || packageFramework.IsAgnostic)
        {
            return true;
        }
        
        return false;
    }

    private async Task<string?> ResolveVersionRangeAsync(string packageId, NuGet.Versioning.VersionRange versionRange)
    {
        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            var versions = await resource.GetAllVersionsAsync(
                packageId,
                cache,
                _logger,
                CancellationToken.None);

            // Find the best version that satisfies the range
            var matchingVersions = versions
                .Where(v => versionRange.Satisfies(v))
                .OrderByDescending(v => v)
                .ToList();

            // Prefer stable versions
            var stableVersion = matchingVersions.FirstOrDefault(v => !v.IsPrerelease);
            if (stableVersion != null)
            {
                return stableVersion.ToString();
            }

            // Fall back to any version
            return matchingVersions.FirstOrDefault()?.ToString();
        }
        catch
        {
            // If we can't resolve, use the minimum version if specified
            return versionRange.MinVersion?.ToString();
        }
    }

    private string? FindExternalPdb(string assemblyPath, string pdbFileName, string workingDirectory)
    {
        // Extract the TFM from the assembly path
        // Assembly path is typically: workingDirectory/extracted/lib/netX.X/PackageName.dll
        var extractedDir = Path.Combine(workingDirectory, "extracted");
        string? targetFramework = null;
        
        if (assemblyPath.StartsWith(extractedDir))
        {
            var relativePath = Path.GetRelativePath(extractedDir, assemblyPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // parts[0] = "lib" or "ref"
            // parts[1] = TFM like "net6.0" or "netstandard2.0"
            if (parts.Length > 1)
            {
                targetFramework = parts[1];
            }
        }

        // Try multiple locations:
        // 1. Same directory as assembly (highest priority)
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (assemblyDir != null)
        {
            var pdbPath = Path.Combine(assemblyDir, Path.GetFileName(pdbFileName));
            if (File.Exists(pdbPath))
                return pdbPath;
        }

        // 2. Symbols package extraction directory - prefer matching TFM
        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (Directory.Exists(symbolsDir))
        {
            var pdbPaths = Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories);
            
            // First, try to find PDB with matching TFM
            if (targetFramework != null)
            {
                var tfmMatch = pdbPaths.FirstOrDefault(p =>
                {
                    var name = Path.GetFileName(p);
                    if (!name.Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase))
                        return false;
                    
                    // Check if this PDB is under a directory matching our TFM
                    var pdbRelativePath = Path.GetRelativePath(symbolsDir, p);
                    return pdbRelativePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase);
                });
                
                if (tfmMatch != null)
                    return tfmMatch;
            }
            
            // Fallback: find any PDB with matching name
            var match = pdbPaths.FirstOrDefault(p => 
                Path.GetFileName(p).Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // 3. Original package extraction directory - prefer matching TFM
        if (Directory.Exists(extractedDir))
        {
            var pdbPaths = Directory.GetFiles(extractedDir, "*.pdb", SearchOption.AllDirectories);
            
            // First, try to find PDB with matching TFM
            if (targetFramework != null)
            {
                var tfmMatch = pdbPaths.FirstOrDefault(p =>
                {
                    var name = Path.GetFileName(p);
                    if (!name.Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase))
                        return false;
                    
                    // Check if this PDB is under a directory matching our TFM
                    var pdbRelativePath = Path.GetRelativePath(extractedDir, p);
                    return pdbRelativePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase);
                });
                
                if (tfmMatch != null)
                    return tfmMatch;
            }
            
            // Fallback: find any PDB with matching name
            var match = pdbPaths.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return null;
    }

    private async Task<bool> TryDownloadSymbolsPackageAsync(string assemblyPath, string workingDirectory)
    {
        try
        {
            // Extract package info from the assembly path
            // Path is typically: workingDirectory/extracted/lib/netX.X/PackageName.dll
            var extractedDir = Path.Combine(workingDirectory, "extracted");
            if (!assemblyPath.StartsWith(extractedDir))
            {
                return false; // Not from a NuGet package
            }

            // Try to find the .nuspec file to get package ID and version
            var nuspecFiles = Directory.GetFiles(extractedDir, "*.nuspec", SearchOption.TopDirectoryOnly);
            if (nuspecFiles.Length == 0)
            {
                return false;
            }

            var nuspecPath = nuspecFiles[0];
            var (packageId, version) = ParseNuspecFile(nuspecPath);
            
            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(version))
            {
                return false;
            }

            AnsiConsole.MarkupLine($"    [dim]Trying to download {packageId}.{version}.snupkg...[/]");

            // Try multiple symbol package sources
            var snupkgUrls = new[]
            {
                $"https://www.nuget.org/api/v2/symbolpackage/{packageId}/{version}",
                $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.snupkg",
                $"https://globalcdn.nuget.org/packages/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.snupkg",
            };

            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            foreach (var snupkgUrl in snupkgUrls)
            {
                try
                {
                    var response = await httpClient.GetAsync(snupkgUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var snupkgPath = Path.Combine(workingDirectory, $"{packageId}.{version}.snupkg");
                        await using (var fileStream = File.Create(snupkgPath))
                        {
                            await response.Content.CopyToAsync(fileStream);
                        }

                        // Extract the symbols package
                        var symbolsDir = Path.Combine(workingDirectory, "symbols");
                        Directory.CreateDirectory(symbolsDir);
                        await ZipFile.ExtractToDirectoryAsync(snupkgPath, symbolsDir, overwriteFiles: true);

                        var pdbCount = Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories).Length;
                        AnsiConsole.MarkupLine($"    [green]✓ Downloaded and extracted symbols package ({pdbCount} PDB files)[/]");
                        return true;
                    }
                }
                catch
                {
                    // Try next URL
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private (string packageId, string version) ParseNuspecFile(string nuspecPath)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(nuspecPath);
            var ns = doc.Root?.Name.Namespace;
            if (ns == null) return ("", "");
            
            var metadata = doc.Root?.Element(ns + "metadata");
            
            var packageId = metadata?.Element(ns + "id")?.Value ?? "";
            var version = metadata?.Element(ns + "version")?.Value ?? "";
            
            return (packageId, version);
        }
        catch
        {
            return ("", "");
        }
    }
}
