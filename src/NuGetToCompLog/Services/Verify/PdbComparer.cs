using System.Reflection.Metadata;

namespace NuGetToCompLog.Services.Verify;

/// <summary>
/// Explains *why* two portable PDBs differ in terms of the actionable root causes we know
/// about: document checksums (wrong source bytes), the metadata-references blob (wrong
/// reference assemblies), Source Link presence, and embedded-source blobs (e.g. generator
/// outputs embedded with different compression).
/// </summary>
public static class PdbComparer
{
    private static readonly Guid SourceLinkGuid = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid CompilationOptionsGuid = new("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");
    private static readonly Guid MetadataReferencesGuid = new("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D");
    private static readonly Guid EmbeddedSourceGuid = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    public static List<string> Explain(string originalPdbPath, string rebuiltPdbPath)
    {
        var findings = new List<string>();

        try
        {
            using var originalStream = File.OpenRead(originalPdbPath);
            using var rebuiltStream = File.OpenRead(rebuiltPdbPath);
            using var originalProvider = MetadataReaderProvider.FromPortablePdbStream(originalStream);
            using var rebuiltProvider = MetadataReaderProvider.FromPortablePdbStream(rebuiltStream);
            var original = originalProvider.GetMetadataReader();
            var rebuilt = rebuiltProvider.GetMetadataReader();

            CompareDocuments(original, rebuilt, findings);
            CompareModuleCdis(original, rebuilt, findings);
            CompareEmbeddedSources(original, rebuilt, findings);
        }
        catch (Exception ex)
        {
            findings.Add($"could not analyze PDB differences: {ex.Message}");
        }

        return findings;
    }

    private static void CompareDocuments(MetadataReader original, MetadataReader rebuilt, List<string> findings)
    {
        var originalDocs = ReadDocuments(original);
        var rebuiltDocs = ReadDocuments(rebuilt);

        if (originalDocs.Count != rebuiltDocs.Count)
        {
            findings.Add($"document count differs: original {originalDocs.Count}, rebuilt {rebuiltDocs.Count}");
            return;
        }

        var orderMismatch = false;
        var hashMismatches = new List<string>();
        for (var i = 0; i < originalDocs.Count; i++)
        {
            if (!string.Equals(originalDocs[i].Name, rebuiltDocs[i].Name, StringComparison.Ordinal))
            {
                orderMismatch = true;
            }
            else if (!originalDocs[i].Hash.AsSpan().SequenceEqual(rebuiltDocs[i].Hash))
            {
                hashMismatches.Add(originalDocs[i].Name);
            }
        }

        if (orderMismatch)
        {
            findings.Add("document names/order differ - sources were compiled in a different order or from different paths");
        }
        foreach (var name in hashMismatches.Take(5))
        {
            findings.Add($"document checksum mismatch (source bytes differ, e.g. line endings): {name}");
        }
        if (hashMismatches.Count > 5)
        {
            findings.Add($"... and {hashMismatches.Count - 5} more document checksum mismatches");
        }
    }

    private static List<(string Name, byte[] Hash)> ReadDocuments(MetadataReader reader) =>
        reader.Documents
            .Select(handle =>
            {
                var doc = reader.GetDocument(handle);
                return (reader.GetString(doc.Name), doc.Hash.IsNil ? [] : reader.GetBlobBytes(doc.Hash));
            })
            .ToList();

    private static void CompareModuleCdis(MetadataReader original, MetadataReader rebuilt, List<string> findings)
    {
        var originalCdis = ReadModuleCdis(original);
        var rebuiltCdis = ReadModuleCdis(rebuilt);

        foreach (var kind in originalCdis.Keys.Union(rebuiltCdis.Keys))
        {
            var name = kind == SourceLinkGuid ? "Source Link"
                : kind == CompilationOptionsGuid ? "compilation options"
                : kind == MetadataReferencesGuid ? "metadata references (reference assembly identities)"
                : kind.ToString();

            var inOriginal = originalCdis.TryGetValue(kind, out var originalBlob);
            var inRebuilt = rebuiltCdis.TryGetValue(kind, out var rebuiltBlob);

            if (inOriginal != inRebuilt)
            {
                findings.Add($"{name} blob {(inOriginal ? "missing from rebuilt" : "unexpectedly present in rebuilt")} PDB");
            }
            else if (inOriginal && !originalBlob.AsSpan().SequenceEqual(rebuiltBlob))
            {
                findings.Add($"{name} blob differs ({originalBlob!.Length} vs {rebuiltBlob!.Length} bytes)");
            }
        }
    }

    private static Dictionary<Guid, byte[]> ReadModuleCdis(MetadataReader reader)
    {
        var result = new Dictionary<Guid, byte[]>();
        foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = reader.GetCustomDebugInformation(handle);
            if (!cdi.Kind.IsNil)
            {
                result[reader.GetGuid(cdi.Kind)] = reader.GetBlobBytes(cdi.Value);
            }
        }
        return result;
    }

    private static void CompareEmbeddedSources(MetadataReader original, MetadataReader rebuilt, List<string> findings)
    {
        var originalEmbedded = ReadEmbeddedSizes(original);
        var rebuiltEmbedded = ReadEmbeddedSizes(rebuilt);

        foreach (var name in originalEmbedded.Keys.Union(rebuiltEmbedded.Keys))
        {
            var inOriginal = originalEmbedded.TryGetValue(name, out var originalSize);
            var inRebuilt = rebuiltEmbedded.TryGetValue(name, out var rebuiltSize);

            if (inOriginal != inRebuilt)
            {
                findings.Add($"embedded source {(inOriginal ? "missing from rebuilt" : "unexpectedly present in rebuilt")} PDB: {name}");
            }
            else if (originalSize != rebuiltSize)
            {
                findings.Add($"embedded source blob size differs for {name} ({originalSize} vs {rebuiltSize} bytes) - " +
                             "generator-produced sources are compressed differently when passed as plain files");
            }
        }
    }

    private static Dictionary<string, int> ReadEmbeddedSizes(MetadataReader reader)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var docHandle in reader.Documents)
        {
            foreach (var cdiHandle in reader.GetCustomDebugInformation(docHandle))
            {
                var cdi = reader.GetCustomDebugInformation(cdiHandle);
                if (!cdi.Kind.IsNil && reader.GetGuid(cdi.Kind) == EmbeddedSourceGuid)
                {
                    result[reader.GetString(reader.GetDocument(docHandle).Name)] = reader.GetBlobBytes(cdi.Value).Length;
                }
            }
        }
        return result;
    }
}
