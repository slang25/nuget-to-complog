using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

        var maskedOriginal = (byte[])original.Clone();
        var maskedRebuilt = (byte[])rebuilt.Clone();
        var derivedDiffs = new List<string>();

        // Mask each file's own derived regions and compare the extracted region bytes per name,
        // so classification works even when the files' offsets have diverged.
        foreach (var (name, regionsA, regionsB) in ZipRegions(original, rebuilt))
        {
            var bytesA = ConcatRegions(original, regionsA);
            var bytesB = ConcatRegions(rebuilt, regionsB);
            foreach (var (start, length) in regionsA)
            {
                maskedOriginal.AsSpan(start, length).Clear();
            }
            foreach (var (start, length) in regionsB)
            {
                maskedRebuilt.AsSpan(start, length).Clear();
            }
            if (!bytesA.AsSpan().SequenceEqual(bytesB))
            {
                derivedDiffs.Add(name);
            }
        }

        // Authenticode signatures are applied to the finished binary (appended at the end of
        // the file); a rebuild can never reproduce one without the publisher's key. Splice
        // them out and report them rather than presenting the tail as opaque content drift.
        var spliced = new List<string>();
        var originalCert = FindCertificateSpan(original);
        var rebuiltCert = FindCertificateSpan(rebuilt);
        if (originalCert != null || rebuiltCert != null)
        {
            spliced.Add("the Authenticode signature");
            derivedDiffs.Add($"Authenticode signature (original {originalCert?.Length ?? 0:N0} bytes, " +
                             $"rebuilt {rebuiltCert?.Length ?? 0:N0} bytes) - applied after compilation, " +
                             "not reproducible without the publisher's key");
            if (originalCert is { } oc)
            {
                ClearCertificateDirectoryEntry(maskedOriginal);
                maskedOriginal = Splice(maskedOriginal, oc);
            }
            if (rebuiltCert is { } rc)
            {
                ClearCertificateDirectoryEntry(maskedRebuilt);
                maskedRebuilt = Splice(maskedRebuilt, rc);
            }
        }

        // The embedded portable PDB blob is assembly content, but like an external PDB it
        // drifts for its own reasons (and its size shifts everything after it). Splice it out
        // of both files, report it as its own difference, and classify the rest.
        var originalPdbSpan = FindEmbeddedPdbSpan(original);
        var rebuiltPdbSpan = FindEmbeddedPdbSpan(rebuilt);
        if (originalPdbSpan is { } os && rebuiltPdbSpan is { } rs &&
            !original.AsSpan(os.Start, os.Length).SequenceEqual(rebuilt.AsSpan(rs.Start, rs.Length)))
        {
            derivedDiffs.Add($"embedded portable PDB ({os.Length:N0} vs {rs.Length:N0} bytes compressed)");
            if (os.Length != rs.Length)
            {
                // A size change shifts everything the linker lays out after the blob: PE size
                // fields, the entry-point/import stub RVAs, and the .reloc fixup for the stub.
                // Mask those and splice through to the end of the enclosing section so equal
                // content re-aligns.
                foreach (var (start, length) in GetLayoutDerivedRegions(original))
                {
                    maskedOriginal.AsSpan(start, length).Clear();
                }
                foreach (var (start, length) in GetLayoutDerivedRegions(rebuilt))
                {
                    maskedRebuilt.AsSpan(start, length).Clear();
                }
                os = ExtendToSectionEnd(original, os);
                rs = ExtendToSectionEnd(rebuilt, rs);
            }
            maskedOriginal = Splice(maskedOriginal, os);
            maskedRebuilt = Splice(maskedRebuilt, rs);
            spliced.Add("the embedded PDB");
        }

        if (maskedOriginal.Length != maskedRebuilt.Length)
        {
            // Name what was removed: a signed original is ~10KB larger on disk than its own
            // content, so reporting these as raw "file sizes" makes a small content delta look
            // like a large one.
            var what = spliced.Count == 0
                ? "file sizes differ"
                : $"content sizes differ after removing {string.Join(" and ", spliced)}";
            return new ComparisonResult(false, false, derivedDiffs,
                [$"{what}: original {maskedOriginal.Length:N0} bytes, rebuilt {maskedRebuilt.Length:N0} bytes"]);
        }

        if (maskedOriginal.AsSpan().SequenceEqual(maskedRebuilt))
        {
            return new ComparisonResult(false, true, derivedDiffs, []);
        }

        return new ComparisonResult(false, false, derivedDiffs, ClusterDifferences(maskedOriginal, maskedRebuilt));
    }

    /// <summary>
    /// Extracts and inflates the embedded portable PDB from an assembly so it can be compared
    /// like an external one. Returns false when the assembly has none.
    /// </summary>
    public static bool TryExtractEmbeddedPdb(string assemblyPath, string outputPath)
    {
        try
        {
            var file = File.ReadAllBytes(assemblyPath);
            if (FindEmbeddedPdbSpan(file) is not { } span)
            {
                return false;
            }

            // Blob layout: "MPDB" magic, uint32 uncompressed size, raw-deflate portable PDB.
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(span.Start)) != 0x4244504D)
            {
                return false;
            }
            using var deflate = new System.IO.Compression.DeflateStream(
                new MemoryStream(file, span.Start + 8, span.Length - 8),
                System.IO.Compression.CompressionMode.Decompress);
            using var output = File.Create(outputPath);
            deflate.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ConcatRegions(byte[] file, List<(int Start, int Length)> regions)
    {
        var result = new byte[regions.Sum(r => r.Length)];
        var offset = 0;
        foreach (var (start, length) in regions)
        {
            file.AsSpan(start, length).CopyTo(result.AsSpan(offset));
            offset += length;
        }
        return result;
    }

    private static (int Start, int Length)? FindEmbeddedPdbSpan(byte[] file)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var entry = peReader.ReadDebugDirectory()
                .FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            return entry.DataSize == 0 ? null : (entry.DataPointer, entry.DataSize);
        }
        catch
        {
            return null;
        }
    }

    private static (int Start, int Length)? FindCertificateSpan(byte[] file)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var directory = peReader.PEHeaders.PEHeader!.CertificateTableDirectory;
            // The certificate table's "RVA" is actually a file offset.
            return directory.Size == 0 ? null : (directory.RelativeVirtualAddress, directory.Size);
        }
        catch
        {
            return null;
        }
    }

    private static void ClearCertificateDirectoryEntry(byte[] file)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var headers = peReader.PEHeaders;
            var dataDirectoriesStart = headers.CoffHeaderStartOffset + 20 +
                (headers.PEHeader!.Magic == PEMagic.PE32 ? 96 : 112);
            file.AsSpan(dataDirectoriesStart + 4 * 8, 8).Clear();
        }
        catch
        {
        }
    }

    private static (int Start, int Length) ExtendToSectionEnd(byte[] file, (int Start, int Length) span)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            foreach (var section in peReader.PEHeaders.SectionHeaders)
            {
                if (section.PointerToRawData <= span.Start &&
                    span.Start < section.PointerToRawData + section.SizeOfRawData)
                {
                    return (span.Start, section.PointerToRawData + section.SizeOfRawData - span.Start);
                }
            }
        }
        catch
        {
        }
        return span;
    }

    private static byte[] Splice(byte[] file, (int Start, int Length) span)
    {
        var result = new byte[file.Length - span.Length];
        file.AsSpan(0, span.Start).CopyTo(result);
        file.AsSpan(span.Start + span.Length).CopyTo(result.AsSpan(span.Start));
        return result;
    }

    /// <summary>
    /// PE fields that shift when the embedded PDB blob changes size. Beyond the optional
    /// header's size fields and entry point, a size change that crosses a section- or
    /// file-alignment boundary relocates every section laid out after .text, so this covers
    /// the whole of each section header's location (VirtualSize / VirtualAddress /
    /// SizeOfRawData / PointerToRawData), the data directories that point at those sections
    /// (import table / IAT, resources, base relocations, debug), the RVAs the resource
    /// directory stores for its own data, and the .reloc section (whose only fixup targets
    /// the import stub).
    /// </summary>
    private static List<(int Start, int Length)> GetLayoutDerivedRegions(byte[] file)
    {
        var regions = new List<(int, int)>();
        void Add(int start, int length)
        {
            if (start >= 0 && length > 0 && start + length <= file.Length)
            {
                regions.Add((start, length));
            }
        }

        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var headers = peReader.PEHeaders;
            var optionalHeaderStart = headers.CoffHeaderStartOffset + 20;
            Add(optionalHeaderStart + 4, 8);   // SizeOfCode + SizeOfInitializedData
            Add(optionalHeaderStart + 16, 4);  // AddressOfEntryPoint
            Add(optionalHeaderStart + 56, 4);  // SizeOfImage
            if (headers.PEHeader!.Magic == PEMagic.PE32)
            {
                // BaseOfData (PE32 only) is the RVA of the first section after .text.
                Add(optionalHeaderStart + 24, 4);
            }

            var dataDirectoriesStart = optionalHeaderStart +
                (headers.PEHeader.Magic == PEMagic.PE32 ? 96 : 112);
            // Every directory entry that addresses relocatable content: the import table (laid
            // out right after the debug data), the IAT, and the resource / base-relocation /
            // debug directories, which move wholesale when their section shifts.
            foreach (var directory in (int[])[1, 2, 5, 6, 12])
            {
                Add(dataDirectoriesStart + directory * 8, 8);
            }

            // The IAT data itself (at the start of .text) holds a thunk RVA into the import
            // table, which sits after the debug data and therefore shifts too.
            if (headers.TryGetDirectoryOffset(headers.PEHeader.ImportAddressTableDirectory, out var iatOffset))
            {
                Add(iatOffset, headers.PEHeader.ImportAddressTableDirectory.Size);
            }

            var sectionHeadersStart = optionalHeaderStart + headers.CoffHeader.SizeOfOptionalHeader;
            for (var i = 0; i < headers.SectionHeaders.Length; i++)
            {
                // VirtualSize, VirtualAddress, SizeOfRawData and PointerToRawData are four
                // consecutive uint32s starting at offset 8 of the 40-byte section header.
                Add(sectionHeadersStart + i * 40 + 8, 16);
                if (headers.SectionHeaders[i].Name == ".reloc")
                {
                    Add(headers.SectionHeaders[i].PointerToRawData, headers.SectionHeaders[i].SizeOfRawData);
                }
            }

            AddResourceDataRvas(file, headers, Add);

            // FieldRVA rows point at mapped static data (e.g. array initializers), which the
            // compiler lays out after the debug data - those RVAs shift with the blob too.
            var metadataReader = peReader.GetMetadataReader();
            var fieldRvaRows = metadataReader.GetTableRowCount(TableIndex.FieldRva);
            if (fieldRvaRows > 0)
            {
                var tableStart = headers.MetadataStartOffset +
                    metadataReader.GetTableMetadataOffset(TableIndex.FieldRva);
                var rowSize = metadataReader.GetTableRowSize(TableIndex.FieldRva);
                for (var row = 0; row < fieldRvaRows; row++)
                {
                    Add(tableStart + row * rowSize, 4);
                }
            }
        }
        catch
        {
        }
        return regions;
    }

    /// <summary>
    /// Win32 resources (the version block the SDK emits by default) address their own payloads
    /// by RVA, so when .rsrc relocates every IMAGE_RESOURCE_DATA_ENTRY.OffsetToData changes
    /// while the resource content itself is identical. Walks the resource directory tree and
    /// reports those four-byte RVAs.
    /// </summary>
    private static void AddResourceDataRvas(byte[] file, PEHeaders headers, Action<int, int> add)
    {
        if (!headers.TryGetDirectoryOffset(headers.PEHeader!.ResourceTableDirectory, out var resourceBase))
        {
            return;
        }

        var resourceEnd = resourceBase + headers.PEHeader.ResourceTableDirectory.Size;
        if (resourceEnd > file.Length)
        {
            return;
        }

        var visited = new HashSet<int>();
        WalkDirectory(resourceBase, 0);

        void WalkDirectory(int directoryOffset, int depth)
        {
            // The format allows three levels (type / name / language); the depth bound also
            // stops a malformed file's cyclic offsets.
            if (depth > 3 || directoryOffset + 16 > resourceEnd || !visited.Add(directoryOffset))
            {
                return;
            }

            var entryCount =
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(directoryOffset + 12)) +
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(directoryOffset + 14));
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = directoryOffset + 16 + i * 8;
                if (entryOffset + 8 > resourceEnd)
                {
                    return;
                }

                var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(entryOffset + 4));
                var target = resourceBase + (int)(value & 0x7FFFFFFF);
                if ((value & 0x80000000) != 0)
                {
                    WalkDirectory(target, depth + 1);
                }
                else if (target + 16 <= resourceEnd)
                {
                    // IMAGE_RESOURCE_DATA_ENTRY: OffsetToData (an RVA), Size, CodePage, Reserved.
                    add(target, 4);
                }
            }
        }
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
