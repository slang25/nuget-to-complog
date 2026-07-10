using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Services.Verify;

public record ComparisonResult(
    bool ExactMatch,
    bool DerivedOnly,
    List<string> DerivedDifferences,
    List<string> RealDifferences)
{
    public static ComparisonResult Exact() => new(true, false, [], []);
}

/// <summary>
/// Byte-compares an original and rebuilt assembly, distinguishing "real" content differences
/// from fields that are *derived* from content under deterministic compilation: the COFF
/// timestamp, PE checksum, MVID, strong-name signature, and the debug directory's CodeView
/// GUID / PdbChecksum payloads (which come from the PDB). A rebuild that differs only in
/// derived fields has reproduced the compilation content exactly — the remaining drift always
/// traces back to the PDB or the signing key.
/// </summary>
public static class BinaryDiffClassifier
{
    public static ComparisonResult CompareAssemblies(string originalPath, string rebuiltPath)
    {
        var original = File.ReadAllBytes(originalPath);
        var rebuilt = File.ReadAllBytes(rebuiltPath);

        if (original.AsSpan().SequenceEqual(rebuilt))
        {
            return ComparisonResult.Exact();
        }

        if (original.Length != rebuilt.Length)
        {
            return new ComparisonResult(false, false, [],
                [$"file sizes differ: original {original.Length:N0} bytes, rebuilt {rebuilt.Length:N0} bytes"]);
        }

        var maskedOriginal = (byte[])original.Clone();
        var maskedRebuilt = (byte[])rebuilt.Clone();
        var derivedDiffs = new List<string>();

        foreach (var (name, regionsA, regionsB) in ZipRegions(original, rebuilt))
        {
            var differed = false;
            foreach (var (start, length) in regionsA)
            {
                if (!original.AsSpan(start, length).SequenceEqual(rebuilt.AsSpan(start, length)))
                {
                    differed = true;
                }
                maskedOriginal.AsSpan(start, length).Clear();
            }
            foreach (var (start, length) in regionsB)
            {
                maskedRebuilt.AsSpan(start, length).Clear();
            }
            if (differed)
            {
                derivedDiffs.Add(name);
            }
        }

        if (maskedOriginal.AsSpan().SequenceEqual(maskedRebuilt))
        {
            return new ComparisonResult(false, true, derivedDiffs, []);
        }

        return new ComparisonResult(false, false, derivedDiffs, ClusterDifferences(maskedOriginal, maskedRebuilt));
    }

    public static ComparisonResult ComparePdbs(string originalPath, string rebuiltPath)
    {
        var original = File.ReadAllBytes(originalPath);
        var rebuilt = File.ReadAllBytes(rebuiltPath);

        if (original.AsSpan().SequenceEqual(rebuilt))
        {
            return ComparisonResult.Exact();
        }

        var real = original.Length != rebuilt.Length
            ? new List<string> { $"file sizes differ: original {original.Length:N0} bytes, rebuilt {rebuilt.Length:N0} bytes" }
            : ClusterDifferences(original, rebuilt);
        real.AddRange(PdbComparer.Explain(originalPath, rebuiltPath));
        return new ComparisonResult(false, false, [], real);
    }

    private static IEnumerable<(string Name, List<(int Start, int Length)> A, List<(int Start, int Length)> B)> ZipRegions(
        byte[] original, byte[] rebuilt)
    {
        var regionsA = GetDerivedRegions(original);
        var regionsB = GetDerivedRegions(rebuilt);
        foreach (var name in regionsA.Keys.Union(regionsB.Keys))
        {
            yield return (name,
                regionsA.GetValueOrDefault(name, []),
                regionsB.GetValueOrDefault(name, []));
        }
    }

    private static Dictionary<string, List<(int Start, int Length)>> GetDerivedRegions(byte[] file)
    {
        var regions = new Dictionary<string, List<(int, int)>>();
        void Add(string name, int start, int length)
        {
            if (start < 0 || length <= 0 || start + length > file.Length)
            {
                return;
            }
            if (!regions.TryGetValue(name, out var list))
            {
                regions[name] = list = [];
            }
            list.Add((start, length));
        }

        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var headers = peReader.PEHeaders;

            Add("COFF timestamp", headers.CoffHeaderStartOffset + 4, 4);
            Add("PE checksum", headers.PEHeaderStartOffset + 64, 4);

            if (headers.CorHeader is { } corHeader &&
                headers.TryGetDirectoryOffset(corHeader.StrongNameSignatureDirectory, out var snOffset))
            {
                Add("strong-name signature", snOffset, corHeader.StrongNameSignatureDirectory.Size);
            }

            if (headers.PEHeader is { } peHeader &&
                headers.TryGetDirectoryOffset(peHeader.DebugTableDirectory, out var debugDirOffset))
            {
                // Table entries embed a per-entry timestamp.
                Add("debug directory table", debugDirOffset, peHeader.DebugTableDirectory.Size);
            }

            foreach (var entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type is DebugDirectoryEntryType.CodeView)
                {
                    Add("CodeView PDB id", entry.DataPointer, entry.DataSize);
                }
                else if (entry.Type is DebugDirectoryEntryType.PdbChecksum)
                {
                    Add("PDB checksum", entry.DataPointer, entry.DataSize);
                }
            }

            // The MVID lives in the #GUID heap; under /deterministic it's derived from the
            // content hash. Locate it by its byte pattern (16 random bytes - collisions are
            // not a practical concern).
            var metadataReader = peReader.GetMetadataReader();
            var mvid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid).ToByteArray();
            var index = file.AsSpan().IndexOf(mvid);
            while (index >= 0)
            {
                Add("MVID", index, 16);
                var next = file.AsSpan(index + 16).IndexOf(mvid);
                index = next < 0 ? -1 : index + 16 + next;
            }
        }
        catch
        {
            // Unparseable PE: no regions, compare raw.
        }

        return regions;
    }

    private static List<string> ClusterDifferences(byte[] a, byte[] b, int maxClusters = 10)
    {
        var clusters = new List<string>();
        int? clusterStart = null;
        var lastDiff = -100;

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                continue;
            }
            if (clusterStart == null)
            {
                clusterStart = i;
            }
            else if (i - lastDiff > 16)
            {
                clusters.Add(Describe(clusterStart.Value, lastDiff));
                clusterStart = i;
                if (clusters.Count >= maxClusters)
                {
                    clusters.Add("...");
                    return clusters;
                }
            }
            lastDiff = i;
        }

        if (clusterStart != null)
        {
            clusters.Add(Describe(clusterStart.Value, lastDiff));
        }

        return clusters;

        static string Describe(int start, int end) =>
            $"bytes differ at 0x{start:x6}-0x{end:x6} ({end - start + 1} bytes)";
    }
}
