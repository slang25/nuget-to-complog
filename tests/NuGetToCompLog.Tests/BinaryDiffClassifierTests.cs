using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using NuGetToCompLog.Services.Verify;
using Xunit;

namespace NuGetToCompLog.Tests;

public class BinaryDiffClassifierTests
{
    /// <summary>
    /// Emitting the same compilation with and without embedded source text produces identical
    /// IL and metadata but a much larger embedded PDB. The size change crosses both alignment
    /// boundaries, so .rsrc and .reloc move in the raw and the virtual address space alike.
    /// Everything that moves is a consequence of the PDB size, so the comparison must come back
    /// derived-only rather than reporting the shifted section headers, optional-header fields,
    /// directory RVAs and resource RVAs as content drift.
    /// </summary>
    [Fact]
    public void SectionShiftFromEmbeddedPdbSizeIsDerivedNotRealDrift()
    {
        var directory = Directory.CreateTempSubdirectory("bindiff").FullName;
        try
        {
            var withoutEmbedding = Path.Combine(directory, "a.dll");
            var withEmbedding = Path.Combine(directory, "b.dll");
            Emit(withoutEmbedding, embedSource: false);
            Emit(withEmbedding, embedSource: true);

            AssertSectionsMoved(withoutEmbedding, withEmbedding);

            var result = BinaryDiffClassifier.CompareAssemblies(withoutEmbedding, withEmbedding);

            Assert.False(result.ExactMatch);
            Assert.True(result.DerivedOnly, "real differences: " + string.Join(", ", result.RealDifferences));
            Assert.Contains(result.DerivedDifferences, d => d.StartsWith("embedded portable PDB"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Emit(string outputPath, bool embedSource)
    {
        // Comment padding that barely compresses, so embedding it grows .text past the next
        // section-alignment boundary and relocates the sections laid out after it.
        var random = new Random(Seed: 7);
        var padding = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 600).Select(_ => "// " + string.Concat(
                Enumerable.Range(0, 70).Select(_ => (char)('a' + random.Next(26))))));
        var text = SourceText.From($$"""
            public class C
            {
                private static readonly int[] Data = { 1, 2, 3, 4, 5, 6, 7, 8 };
                public static int Sum() { var total = 0; foreach (var d in Data) total += d; return total; }
            }
            {{padding}}
            """, Encoding.UTF8);

        var tree = CSharpSyntaxTree.ParseText(text, path: "/src/C.cs");
        var compilation = CSharpCompilation.Create(
            "BinDiffSample",
            [tree],
            [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));

        using var peStream = File.Create(outputPath);
        var result = compilation.Emit(
            peStream,
            // A Win32 version resource gives the assembly a .rsrc section, whose resource data
            // entries store RVAs that shift with the section.
            win32Resources: compilation.CreateDefaultWin32Resources(
                versionResource: true, noManifest: true, manifestContents: null, iconInIcoFormat: null),
            embeddedTexts: embedSource ? [EmbeddedText.FromSource(tree.FilePath, text)] : null,
            options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.Embedded,
                fileAlignment: 512));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    /// <summary>
    /// Guards the premise of the test: the two emits must actually relocate the sections after
    /// .text, in both the virtual and the raw address space, so the comparison has to deal with
    /// shifted section headers, directory RVAs and resource RVAs.
    /// </summary>
    private static void AssertSectionsMoved(string first, string second)
    {
        var before = SectionsAfterText(first);
        var after = SectionsAfterText(second);

        Assert.NotEmpty(before);
        Assert.Equal(before.Select(s => s.Name), after.Select(s => s.Name));
        Assert.All(
            before.Zip(after),
            pair =>
            {
                Assert.NotEqual(pair.First.VirtualAddress, pair.Second.VirtualAddress);
                Assert.NotEqual(pair.First.PointerToRawData, pair.Second.PointerToRawData);
            });

        static List<SectionHeader> SectionsAfterText(string path)
        {
            using var reader = new PEReader(File.OpenRead(path));
            return reader.PEHeaders.SectionHeaders.Where(s => s.Name != ".text").ToList();
        }
    }
}
