using NuGetToCompLog.Services.Patch;

namespace NuGetToCompLog.Tests;

public class PatchApplierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sourceDir;
    private readonly string _outputDir;
    private readonly PatchApplier _applier = new();

    public PatchApplierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"patch-test-{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(_tempDir, "source");
        _outputDir = Path.Combine(_tempDir, "output");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Apply_SimpleModification_Works()
    {
        File.WriteAllLines(Path.Combine(_sourceDir, "file.cs"), ["line1", "line2", "line3"]);

        var patch = """
            --- a/file.cs
            +++ b/file.cs
            @@ -1,3 +1,3 @@
             line1
            -line2
            +CHANGED
             line3
            """;

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.True(result.Success);
        var output = File.ReadAllLines(Path.Combine(_outputDir, "file.cs"));
        Assert.Equal(["line1", "CHANGED", "line3"], output);
    }

    [Fact]
    public void Apply_NewFile_CreatesFile()
    {
        var patch = """
            --- /dev/null
            +++ b/newfile.cs
            @@ -0,0 +1,2 @@
            +hello
            +world
            """;

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_outputDir, "newfile.cs")));
        var output = File.ReadAllLines(Path.Combine(_outputDir, "newfile.cs"));
        Assert.Equal(["hello", "world"], output);
    }

    [Fact]
    public void Apply_DeletedFile_TracksInDeletedFiles()
    {
        File.WriteAllLines(Path.Combine(_sourceDir, "removed.cs"), ["goodbye"]);

        var patch = """
            --- a/removed.cs
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -goodbye
            """;

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.True(result.Success);
        Assert.Contains("removed.cs", result.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(_outputDir, "removed.cs")));
    }

    [Fact]
    public void Apply_CrlfPatch_HandledCorrectly()
    {
        File.WriteAllLines(Path.Combine(_sourceDir, "file.cs"), ["aaa", "bbb", "ccc"]);

        // Simulate CRLF line endings in the patch content
        var patch = "--- a/file.cs\r\n+++ b/file.cs\r\n@@ -1,3 +1,3 @@\r\n aaa\r\n-bbb\r\n+BBB\r\n ccc";

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.True(result.Success);
        var output = File.ReadAllLines(Path.Combine(_outputDir, "file.cs"));
        Assert.Equal(["aaa", "BBB", "ccc"], output);
    }

    [Fact]
    public void Apply_AbsolutePath_Rejected()
    {
        var patch = """
            --- a//etc/passwd
            +++ b//etc/passwd
            @@ -1,1 +1,1 @@
            -safe
            +hacked
            """;

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.False(result.Success);
        Assert.NotEmpty(result.FailedFiles);
    }

    [Fact]
    public void Apply_TraversalPath_Rejected()
    {
        File.WriteAllLines(Path.Combine(_sourceDir, "file.cs"), ["safe"]);

        var patch = """
            --- a/../../../etc/passwd
            +++ b/../../../etc/passwd
            @@ -1,1 +1,1 @@
            -safe
            +hacked
            """;

        var result = _applier.Apply(patch, _sourceDir, _outputDir);

        Assert.False(result.Success);
        Assert.NotEmpty(result.FailedFiles);
    }
}
