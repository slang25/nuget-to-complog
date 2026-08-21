using System.Reflection.Metadata;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Infrastructure.FileSystem;
using NuGetToCompLog.Services.Pdb;
using Xunit;

namespace NuGetToCompLog.Tests;

public class SourceFileDecompilerServiceTests
{
    /// <summary>
    /// The decompiler's whole job is the file split: it used to write the entire decompiled
    /// module into every missing document, so each type was defined once per file and the
    /// rebuild failed with CS0101 before it started. Each type must land in exactly the one
    /// document the PDB says it came from.
    /// </summary>
    [Fact]
    public async Task EachTypeIsWrittenOnlyToItsOwnDocument()
    {
        var directory = Directory.CreateTempSubdirectory("decompile").FullName;
        try
        {
            var (assemblyPath, pdbPath) = Emit(directory);
            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);

            var output = Path.Combine(directory, "out");
            var written = await new SourceFileDecompilerService(new FileSystemService(), new SilentConsole())
                .DecompileMissingFilesAsync(
                    assemblyPath,
                    [
                        new SourceFileInfo("/src/Alpha.cs", null, false, null, LocalPath: "Alpha.cs"),
                        new SourceFileInfo("/src/Beta.cs", null, false, null, LocalPath: "Beta.cs"),
                    ],
                    pdbProvider.GetMetadataReader(),
                    output,
                    allDocumentsMissing: true);

            Assert.Equal(2, written);
            var alpha = await File.ReadAllTextAsync(Path.Combine(output, "Alpha.cs"));
            var beta = await File.ReadAllTextAsync(Path.Combine(output, "Beta.cs"));

            Assert.Contains("class Alpha", alpha);
            Assert.DoesNotContain("class Beta", alpha);
            Assert.Contains("class Beta", beta);
            Assert.DoesNotContain("class Alpha", beta);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A partial type has methods in more than one document. Emitting it into each would
    /// redefine it, so it belongs to the document holding most of its methods - and the other
    /// document must not repeat it.
    /// </summary>
    [Fact]
    public async Task PartialTypeIsWrittenToOneDocumentOnly()
    {
        var directory = Directory.CreateTempSubdirectory("decompile-partial").FullName;
        try
        {
            var (assemblyPath, pdbPath) = EmitPartial(directory);
            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);

            var output = Path.Combine(directory, "out");
            await new SourceFileDecompilerService(new FileSystemService(), new SilentConsole())
                .DecompileMissingFilesAsync(
                    assemblyPath,
                    [
                        new SourceFileInfo("/src/Part1.cs", null, false, null, LocalPath: "Part1.cs"),
                        new SourceFileInfo("/src/Part2.cs", null, false, null, LocalPath: "Part2.cs"),
                    ],
                    pdbProvider.GetMetadataReader(),
                    output,
                    allDocumentsMissing: true);

            var files = new[] { "Part1.cs", "Part2.cs" }
                .Select(f => File.ReadAllText(Path.Combine(output, f)))
                .ToList();

            Assert.Equal(1, files.Count(text => text.Contains("class Split")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// When a partial type spans a document we recovered and one we did not, the recovered file
    /// already declares it. Decompiling the whole metadata type into the missing file would
    /// repeat every member of the recovered part, so the type is left out entirely.
    /// </summary>
    [Fact]
    public async Task PartialTypeSpanningARecoveredDocumentIsNotDecompiled()
    {
        var directory = Directory.CreateTempSubdirectory("decompile-split").FullName;
        try
        {
            // Part2 holds most of the methods, so it wins the vote - and it is the missing one.
            var (assemblyPath, pdbPath) = EmitTrees(directory, "SampleSplit",
            [
                ("/src/Part1.cs", "public partial class Split { public int A() { return 1; } }"),
                ("/src/Part2.cs", "public partial class Split { public int B() { return 2; } public int C() { return 3; } }"),
            ]);
            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);

            var output = Path.Combine(directory, "out");
            var written = await new SourceFileDecompilerService(new FileSystemService(), new SilentConsole())
                .DecompileMissingFilesAsync(
                    assemblyPath,
                    [new SourceFileInfo("/src/Part2.cs", null, false, null, LocalPath: "Part2.cs")],
                    pdbProvider.GetMetadataReader(),
                    output,
                    allDocumentsMissing: false);

            Assert.Equal(1, written);
            var part2 = await File.ReadAllTextAsync(Path.Combine(output, "Part2.cs"));
            Assert.DoesNotContain("class Split", part2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An assembly whose source declares no method bodies (enums, interfaces) has no method
    /// debug info, so no document can claim ownership by vote. Every document is still missing,
    /// so the types have to land somewhere rather than every file being a placeholder comment.
    /// </summary>
    [Fact]
    public async Task TypesWithNoMethodDebugInfoStillLandInADocument()
    {
        var directory = Directory.CreateTempSubdirectory("decompile-nomethods").FullName;
        try
        {
            var (assemblyPath, pdbPath) = EmitTrees(directory, "SampleDeclarative",
            [
                ("/src/Colors.cs", "public enum Colors { Red, Green }"),
                ("/src/IThing.cs", "public interface IThing { int Value { get; } }"),
            ]);
            using var pdbStream = File.OpenRead(pdbPath);
            using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);

            var output = Path.Combine(directory, "out");
            var written = await new SourceFileDecompilerService(new FileSystemService(), new SilentConsole())
                .DecompileMissingFilesAsync(
                    assemblyPath,
                    [
                        new SourceFileInfo("/src/Colors.cs", null, false, null, LocalPath: "Colors.cs"),
                        new SourceFileInfo("/src/IThing.cs", null, false, null, LocalPath: "IThing.cs"),
                    ],
                    pdbProvider.GetMetadataReader(),
                    output,
                    allDocumentsMissing: true);

            Assert.Equal(2, written);
            var all = string.Concat(new[] { "Colors.cs", "IThing.cs" }
                .Select(f => File.ReadAllText(Path.Combine(output, f))));
            Assert.Contains("enum Colors", all);
            Assert.Contains("interface IThing", all);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (string AssemblyPath, string PdbPath) Emit(string directory) =>
        EmitTrees(directory, "Sample",
        [
            ("/src/Alpha.cs", "public class Alpha { public int One() { return 1; } public int Two() { return 2; } }"),
            ("/src/Beta.cs", "public class Beta { public int Three() { return 3; } }"),
        ]);

    private static (string AssemblyPath, string PdbPath) EmitPartial(string directory) =>
        EmitTrees(directory, "SamplePartial",
        [
            ("/src/Part1.cs", "public partial class Split { public int A() { return 1; } public int B() { return 2; } }"),
            ("/src/Part2.cs", "public partial class Split { public int C() { return 3; } }"),
        ]);

    private static (string AssemblyPath, string PdbPath) EmitTrees(
        string directory, string assemblyName, (string Path, string Source)[] sources)
    {
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(SourceText.From(s.Source, Encoding.UTF8), path: s.Path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));

        var assemblyPath = Path.Combine(directory, assemblyName + ".dll");
        var pdbPath = Path.Combine(directory, assemblyName + ".pdb");
        using (var peStream = File.Create(assemblyPath))
        using (var pdbStream = File.Create(pdbPath))
        {
            var result = compilation.Emit(peStream, pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        }

        return (assemblyPath, pdbPath);
    }

    private sealed class SilentConsole : IConsoleWriter
    {
        public void MarkupLine(string markup) { }
        public void WriteLine() { }
        public void WriteException(Exception exception) { }
        public void WritePanel(string header, string content, string? borderColor = null) { }
        public void WriteTree(string rootLabel, Dictionary<string, List<string>> nodes) { }
        public void WriteTable(string[] headers, List<string[]> rows) { }
        public Task ExecuteWithStatusAsync(string status, Func<Task> action) => action();
        public void SetIndeterminateProgress() { }
        public void ClearProgress() { }
    }
}
