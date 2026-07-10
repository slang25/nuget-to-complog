using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace NuGetToCompLog.Services;

/// <summary>
/// Strong-name helpers: extracting the public key from an assembly, deriving the public key
/// blob from an .snk (CAPI key blob), and probing the source repository for a committed .snk.
///
/// The PDB does not record /keyfile, so signing has to be reconstructed from the assembly
/// itself. With just the assembly we can /publicsign (correct public key + StrongNameSigned
/// flag, zeroed signature). Many OSS projects commit their full .snk, and RSA PKCS#1 signing
/// is deterministic — so when the repo key is found, full /keyfile signing reproduces the
/// original signature byte-for-byte.
/// </summary>
public static class StrongNameUtil
{
    public record StrongNameInfo(byte[] PublicKey, bool IsSigned);

    /// <summary>
    /// Reads the public key and StrongNameSigned flag from an assembly. Returns null when the
    /// assembly is not strong-named.
    /// </summary>
    public static StrongNameInfo? TryGetStrongNameInfo(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();
            var assembly = metadataReader.GetAssemblyDefinition();

            if (assembly.PublicKey.IsNil)
            {
                return null;
            }

            var publicKey = metadataReader.GetBlobBytes(assembly.PublicKey);
            if (publicKey.Length == 0)
            {
                return null;
            }

            var isSigned = (peReader.PEHeaders.CorHeader?.Flags & CorFlags.StrongNameSigned) != 0;
            return new StrongNameInfo(publicKey, isSigned);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Derives the strong-name public key blob (the format stored in assembly metadata and
    /// accepted by /publicsign) from an .snk file, which may hold either a full CAPI private
    /// key blob or an already-public key.
    /// </summary>
    public static byte[]? TryDerivePublicKeyBlob(byte[] snk)
    {
        try
        {
            // Already a strong-name public key blob: 12-byte header (sigAlgId, hashAlgId, length)
            // followed by a PUBLICKEYBLOB (type 0x06).
            if (snk.Length > 13 && snk[12] == 0x06 &&
                BitConverter.ToInt32(snk, 8) == snk.Length - 12)
            {
                return snk;
            }

            // CAPI PRIVATEKEYBLOB: BLOBHEADER { bType=0x07, bVersion, reserved, aiKeyAlg } +
            // RSAPUBKEY { magic 'RSA2', bitlen, pubexp } + modulus + private parameters.
            if (snk.Length < 20 || snk[0] != 0x07)
            {
                return null;
            }

            var magic = BitConverter.ToUInt32(snk, 8);
            if (magic != 0x32415352) // 'RSA2'
            {
                return null;
            }

            var bitLen = BitConverter.ToInt32(snk, 12);
            var modulusLength = bitLen / 8;
            if (bitLen <= 0 || snk.Length < 20 + modulusLength)
            {
                return null;
            }

            // Strong-name public key blob:
            //   header: SigAlgID=CALG_RSA_SIGN (0x2400), HashAlgID=CALG_SHA1 (0x8004), blob length
            //   blob:   PUBLICKEYBLOB { 0x06, 0x02, 0x0000, 0x2400 } + 'RSA1' + bitlen + pubexp + modulus
            var blobLength = 20 + modulusLength;
            using var buffer = new MemoryStream(12 + blobLength);
            using var writer = new BinaryWriter(buffer);
            writer.Write(0x00002400);            // SigAlgID: CALG_RSA_SIGN
            writer.Write(0x00008004);            // HashAlgID: CALG_SHA1
            writer.Write(blobLength);
            writer.Write((byte)0x06);            // PUBLICKEYBLOB
            writer.Write((byte)0x02);            // version
            writer.Write((short)0);              // reserved
            writer.Write(0x00002400);            // aiKeyAlg: CALG_RSA_SIGN
            writer.Write(0x31415352);            // 'RSA1'
            writer.Write(bitLen);
            writer.Write(snk.AsSpan(16, 4));     // public exponent
            writer.Write(snk.AsSpan(20, modulusLength));
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Probes the source repository (via the Source Link commit) for a committed .snk whose
    /// public key matches the assembly's. Currently supports GitHub-hosted repos.
    /// </summary>
    public static async Task<byte[]?> TryFindRepoKeyAsync(
        string sourceLinkJson,
        byte[] expectedPublicKey,
        CancellationToken cancellationToken = default)
    {
        var (owner, repo, commit) = ParseGitHubSourceLink(sourceLinkJson);
        if (owner == null || repo == null || commit == null)
        {
            return null;
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("nuget-to-complog");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        try
        {
            var treeUrl = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{commit}?recursive=1";
            using var treeResponse = await httpClient.GetAsync(treeUrl, cancellationToken);
            if (!treeResponse.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await treeResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("tree", out var tree))
            {
                return null;
            }

            var snkPaths = tree.EnumerateArray()
                .Select(e => e.TryGetProperty("path", out var p) ? p.GetString() : null)
                .Where(p => p != null && p.EndsWith(".snk", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            foreach (var path in snkPaths)
            {
                var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commit}/{path}";
                using var response = await httpClient.GetAsync(rawUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var snk = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var derived = TryDerivePublicKeyBlob(snk);
                if (derived != null && derived.AsSpan().SequenceEqual(expectedPublicKey) && snk[0] == 0x07)
                {
                    return snk;
                }
            }
        }
        catch
        {
            // Network/rate-limit failures degrade to /publicsign.
        }

        return null;
    }

    private static (string? Owner, string? Repo, string? Commit) ParseGitHubSourceLink(string sourceLinkJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(sourceLinkJson);
            if (!doc.RootElement.TryGetProperty("documents", out var documents))
            {
                return (null, null, null);
            }

            foreach (var mapping in documents.EnumerateObject())
            {
                var url = mapping.Value.GetString();
                if (url == null)
                {
                    continue;
                }

                // https://raw.githubusercontent.com/{owner}/{repo}/{commit}/*
                var uri = new Uri(url.Replace("*", "placeholder"));
                if (!uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var segments = uri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length >= 3)
                {
                    return (segments[0], segments[1], segments[2]);
                }
            }
        }
        catch
        {
        }

        return (null, null, null);
    }
}
