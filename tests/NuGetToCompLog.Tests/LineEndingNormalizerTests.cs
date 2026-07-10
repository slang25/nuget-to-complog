using System.Security.Cryptography;
using System.Text;
using NuGetToCompLog.Services;
using Xunit;

namespace NuGetToCompLog.Tests;

public class LineEndingNormalizerTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"len-test-{Guid.NewGuid()}.cs");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void MatchingContentIsLeftAlone()
    {
        var content = Encoding.UTF8.GetBytes("line one\nline two\n");
        File.WriteAllBytes(_tempFile, content);

        var result = LineEndingNormalizer.VerifyAndFix(_tempFile, SHA256.HashData(content), "sha256");

        Assert.Equal(SourceHashVerification.Match, result);
        Assert.Equal(content, File.ReadAllBytes(_tempFile));
    }

    [Fact]
    public void ConvertsLfToCrlfWhenHashDemandsIt()
    {
        // The original build compiled a CRLF checkout; Source Link served LF.
        var crlf = Encoding.UTF8.GetBytes("line one\r\nline two\r\n");
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("line one\nline two\n"));

        var result = LineEndingNormalizer.VerifyAndFix(_tempFile, SHA256.HashData(crlf), "sha256");

        Assert.Equal(SourceHashVerification.Fixed, result);
        Assert.Equal(crlf, File.ReadAllBytes(_tempFile));
    }

    [Fact]
    public void ConvertsCrlfToLfWhenHashDemandsIt()
    {
        var lf = Encoding.UTF8.GetBytes("a\nb\n");
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        var result = LineEndingNormalizer.VerifyAndFix(_tempFile, SHA256.HashData(lf), "sha256");

        Assert.Equal(SourceHashVerification.Fixed, result);
        Assert.Equal(lf, File.ReadAllBytes(_tempFile));
    }

    [Fact]
    public void AddsBomWhenHashDemandsIt()
    {
        var withBom = (byte[])[0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x\r\ny\r\n")];
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("x\ny\n"));

        var result = LineEndingNormalizer.VerifyAndFix(_tempFile, SHA256.HashData(withBom), "sha256");

        Assert.Equal(SourceHashVerification.Fixed, result);
        Assert.Equal(withBom, File.ReadAllBytes(_tempFile));
    }

    [Fact]
    public void SupportsSha1Documents()
    {
        var crlf = Encoding.UTF8.GetBytes("a\r\nb");
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("a\nb"));

        var result = LineEndingNormalizer.VerifyAndFix(_tempFile, SHA1.HashData(crlf), "sha1");

        Assert.Equal(SourceHashVerification.Fixed, result);
    }

    [Fact]
    public void ReportsMismatchWhenContentGenuinelyDiffers()
    {
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("completely different\n"));

        var result = LineEndingNormalizer.VerifyAndFix(
            _tempFile, SHA256.HashData(Encoding.UTF8.GetBytes("original\r\n")), "sha256");

        Assert.Equal(SourceHashVerification.Mismatch, result);
    }

    [Fact]
    public void ReportsNoHashWhenPdbHadNone()
    {
        File.WriteAllBytes(_tempFile, [1, 2, 3]);

        Assert.Equal(SourceHashVerification.NoHash, LineEndingNormalizer.VerifyAndFix(_tempFile, null, null));
        Assert.Equal(SourceHashVerification.NoHash, LineEndingNormalizer.VerifyAndFix(_tempFile, [], "sha256"));
    }
}
