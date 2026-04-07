using NuGetToCompLog.Services.Patch;

namespace NuGetToCompLog.Tests;

public class UnifiedDiffGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalDir;
    private readonly string _modifiedDir;
    private readonly UnifiedDiffGenerator _generator = new();

    public UnifiedDiffGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"diff-test-{Guid.NewGuid():N}");
        _originalDir = Path.Combine(_tempDir, "original");
        _modifiedDir = Path.Combine(_tempDir, "modified");
        Directory.CreateDirectory(_originalDir);
        Directory.CreateDirectory(_modifiedDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ModifiedFile_ProducesCorrectHunk()
    {
        File.WriteAllLines(Path.Combine(_originalDir, "file.cs"), ["line1", "line2", "line3"]);
        File.WriteAllLines(Path.Combine(_modifiedDir, "file.cs"), ["line1", "CHANGED", "line3"]);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);

        Assert.Single(result.ModifiedFiles);
        Assert.Equal("file.cs", result.ModifiedFiles[0]);
        var diff = _generator.FormatDiff(result);
        Assert.Contains("-line2", diff);
        Assert.Contains("+CHANGED", diff);
        Assert.Contains("--- a/file.cs", diff);
        Assert.Contains("+++ b/file.cs", diff);
    }

    [Fact]
    public void AddedFile_UsesDevNullHeader()
    {
        File.WriteAllLines(Path.Combine(_modifiedDir, "new.cs"), ["hello", "world"]);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);

        Assert.Single(result.AddedFiles);
        var diff = _generator.FormatDiff(result);
        Assert.Contains("--- /dev/null", diff);
        Assert.Contains("+++ b/new.cs", diff);
        Assert.Contains("+hello", diff);
        Assert.Contains("+world", diff);
    }

    [Fact]
    public void DeletedFile_UsesDevNullHeader()
    {
        File.WriteAllLines(Path.Combine(_originalDir, "removed.cs"), ["goodbye"]);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);

        Assert.Single(result.DeletedFiles);
        var diff = _generator.FormatDiff(result);
        Assert.Contains("--- a/removed.cs", diff);
        Assert.Contains("+++ /dev/null", diff);
        Assert.Contains("-goodbye", diff);
    }

    [Fact]
    public void IdenticalFiles_ProducesNoDiff()
    {
        File.WriteAllLines(Path.Combine(_originalDir, "same.cs"), ["a", "b", "c"]);
        File.WriteAllLines(Path.Combine(_modifiedDir, "same.cs"), ["a", "b", "c"]);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);

        Assert.False(result.HasChanges);
    }

    [Fact]
    public void HunkHeader_HasCorrectLineNumbers()
    {
        var original = Enumerable.Range(1, 10).Select(i => $"line{i}").ToArray();
        var modified = original.ToList();
        modified[4] = "CHANGED"; // line5 → CHANGED

        File.WriteAllLines(Path.Combine(_originalDir, "file.cs"), original);
        File.WriteAllLines(Path.Combine(_modifiedDir, "file.cs"), modified);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);
        var diff = _generator.FormatDiff(result);

        // Should have an @@ header with the right line range
        Assert.Contains("@@", diff);
        Assert.Contains("-line5", diff);
        Assert.Contains("+CHANGED", diff);
    }

    [Fact]
    public void RoundTrip_DiffAndApply_ProducesOriginalModified()
    {
        var original = new[] { "alpha", "beta", "gamma", "delta", "epsilon" };
        var modified = new[] { "alpha", "BETA", "gamma", "delta", "epsilon", "zeta" };

        File.WriteAllLines(Path.Combine(_originalDir, "file.cs"), original);
        File.WriteAllLines(Path.Combine(_modifiedDir, "file.cs"), modified);

        var result = _generator.GenerateDiff(_originalDir, _modifiedDir);
        var diffContent = _generator.FormatDiff(result);

        // Apply the diff
        var applier = new PatchApplier();
        var outputDir = Path.Combine(_tempDir, "output");
        var applyResult = applier.Apply(diffContent, _originalDir, outputDir);

        Assert.True(applyResult.Success);
        var outputLines = File.ReadAllLines(Path.Combine(outputDir, "file.cs"));
        Assert.Equal(modified, outputLines);
    }
}
