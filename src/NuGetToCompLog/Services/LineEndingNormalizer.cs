using System.Security.Cryptography;

namespace NuGetToCompLog.Services;

public enum SourceHashVerification
{
    /// <summary>No hash was recorded in the PDB, nothing to verify against.</summary>
    NoHash,
    /// <summary>File bytes already match the PDB document hash.</summary>
    Match,
    /// <summary>File was rewritten (line endings and/or BOM) to match the PDB document hash.</summary>
    Fixed,
    /// <summary>No line ending / BOM permutation matches — the content genuinely differs.</summary>
    Mismatch,
}

/// <summary>
/// Verifies downloaded source files against the checksums recorded in the PDB Documents table
/// and repairs line-ending/BOM differences.
///
/// Source Link serves files exactly as committed (usually LF, no BOM), but the original build
/// may have compiled a checkout with different line endings (e.g. git autocrlf on a Windows CI
/// runner). That changes the document hash — and therefore the emitted PDB and, through the
/// PdbChecksum debug directory entry, the assembly itself — without changing the IL. Trying
/// the small set of ending/BOM permutations recovers the exact original bytes.
/// </summary>
public static class LineEndingNormalizer
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static SourceHashVerification VerifyAndFix(string filePath, byte[]? expectedHash, string? hashAlgorithm)
    {
        if (expectedHash is not { Length: > 0 })
        {
            return SourceHashVerification.NoHash;
        }

        var original = File.ReadAllBytes(filePath);
        if (HashMatches(original, expectedHash, hashAlgorithm))
        {
            return SourceHashVerification.Match;
        }

        foreach (var candidate in Permutations(original))
        {
            if (HashMatches(candidate, expectedHash, hashAlgorithm))
            {
                File.WriteAllBytes(filePath, candidate);
                return SourceHashVerification.Fixed;
            }
        }

        return SourceHashVerification.Mismatch;
    }

    private static IEnumerable<byte[]> Permutations(byte[] original)
    {
        var lf = ToLf(original);
        var crlf = ToCrLf(lf);

        foreach (var body in new[] { lf, crlf })
        {
            yield return WithoutBom(body);
            yield return WithBom(body);
        }
    }

    private static bool HashMatches(byte[] content, byte[] expected, string? algorithm)
    {
        byte[] actual = algorithm?.ToLowerInvariant() switch
        {
            "sha1" => SHA1.HashData(content),
            _ => SHA256.HashData(content),
        };
        return actual.AsSpan().SequenceEqual(expected);
    }

    private static byte[] ToLf(byte[] content)
    {
        var result = new List<byte>(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\r' && i + 1 < content.Length && content[i + 1] == (byte)'\n')
            {
                continue;
            }
            result.Add(content[i]);
        }
        return [.. result];
    }

    private static byte[] ToCrLf(byte[] lfContent)
    {
        var result = new List<byte>(lfContent.Length + lfContent.Length / 16);
        foreach (var b in lfContent)
        {
            if (b == (byte)'\n')
            {
                result.Add((byte)'\r');
            }
            result.Add(b);
        }
        return [.. result];
    }

    private static byte[] WithBom(byte[] content) =>
        content.AsSpan().StartsWith(Utf8Bom) ? content : [.. Utf8Bom, .. content];

    private static byte[] WithoutBom(byte[] content) =>
        content.AsSpan().StartsWith(Utf8Bom) ? content[3..] : content;
}
