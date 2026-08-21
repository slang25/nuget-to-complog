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
                maskedOriginal = Splice(maskedOriginal, [oc]);
            }
            if (rebuiltCert is { } rc)
            {
                ClearCertificateDirectoryEntry(maskedRebuilt);
                maskedRebuilt = Splice(maskedRebuilt, [rc]);
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
            var originalRemovals = new List<(int Start, int Length)> { os };
            var rebuiltRemovals = new List<(int Start, int Length)> { rs };
            if (os.Length != rs.Length)
            {
                // A size change shifts everything the linker lays out after the blob: PE size
                // fields, the entry-point/import stub RVAs, and the .reloc fixup for the stub.
                // Mask those, then re-align by taking each file's own blob out along with the
                // derived tail of the enclosing section. Splicing straight through from the blob
                // to the end of the section would be simpler, but the mapped field data (array
                // initializers, see the FieldRVA note below) sits between the blob and that tail:
                // removing it would let an initializer change pass as derived-only drift.
                foreach (var (start, length) in GetLayoutDerivedRegions(original))
                {
                    maskedOriginal.AsSpan(start, length).Clear();
                }
                foreach (var (start, length) in GetLayoutDerivedRegions(rebuilt))
                {
                    maskedRebuilt.AsSpan(start, length).Clear();
                }
                var (originalDerived, rebuiltDerived) = FindDerivedRegions(original, os, rebuilt, rs);
                originalRemovals.AddRange(originalDerived);
                rebuiltRemovals.AddRange(rebuiltDerived);
            }
            maskedOriginal = Splice(maskedOriginal, originalRemovals);
            maskedRebuilt = Splice(maskedRebuilt, rebuiltRemovals);
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

    /// <summary>
    /// The regions to drop from each file's <c>.text</c> along with its embedded PDB blob, so
    /// that removing two differently-sized blobs leaves the same number of section bytes on both
    /// sides - without discarding content in the process.
    ///
    /// The compiler lays the section out as [... metadata][debug directory + PDB blob][import
    /// table + hint/name table + startup stub][mapped field data][padding to the file alignment].
    /// The import-table stretch is linker bookkeeping full of RVAs that move with the blob, so it
    /// goes; the mapped field data behind it is real content (array initializers), so it stays
    /// and the trailing zero padding absorbs the rest of the size difference instead. Each side
    /// keeps as many bytes as the *longer* of the two contents needs, so a genuine content-size
    /// change still shows up rather than being padded away.
    /// </summary>
    private static (List<(int Start, int Length)> Original, List<(int Start, int Length)> Rebuilt) FindDerivedRegions(
        byte[] original, (int Start, int Length) originalBlob,
        byte[] rebuilt, (int Start, int Length) rebuiltBlob)
    {
        var originalSection = FindSection(original, originalBlob.Start);
        var rebuiltSection = FindSection(rebuilt, rebuiltBlob.Start);
        if (originalSection == null || rebuiltSection == null)
        {
            return ([], []);
        }

        var originalStub = FindStubRegion(original, originalSection.Value, originalBlob);
        var rebuiltStub = FindStubRegion(rebuilt, rebuiltSection.Value, rebuiltBlob);
        var originalKept = originalSection.Value.Length - originalBlob.Length - (originalStub?.Length ?? 0);
        var rebuiltKept = rebuiltSection.Value.Length - rebuiltBlob.Length - (rebuiltStub?.Length ?? 0);

        var originalPadding = TrailingZeros(original, originalSection.Value, ContentFloor(originalBlob, originalStub));
        var rebuiltPadding = TrailingZeros(rebuilt, rebuiltSection.Value, ContentFloor(rebuiltBlob, rebuiltStub));
        var target = Math.Max(originalKept - originalPadding, rebuiltKept - rebuiltPadding);

        // Never trim more than the zeros actually there: if the two sections cannot be brought
        // to the same length the caller reports a size difference, which is the honest answer.
        var dropOriginal = Math.Clamp(originalKept - target, 0, originalPadding);
        var dropRebuilt = Math.Clamp(rebuiltKept - target, 0, rebuiltPadding);

        return (Regions(originalSection.Value, originalStub, dropOriginal),
                Regions(rebuiltSection.Value, rebuiltStub, dropRebuilt));

        static int ContentFloor((int Start, int Length) blob, (int Start, int Length)? stub) =>
            Math.Max(blob.Start + blob.Length, (stub?.Start ?? 0) + (stub?.Length ?? 0));

        static List<(int Start, int Length)> Regions(
            (int Start, int Length) section, (int Start, int Length)? stub, int padding)
        {
            var regions = new List<(int Start, int Length)>();
            if (stub is { } s)
            {
                regions.Add(s);
            }
            if (padding > 0)
            {
                regions.Add((section.Start + section.Length - padding, padding));
            }
            return regions;
        }
    }

    /// <summary>
    /// The import table, hint/name table and runtime startup stub: everything from the import
    /// directory up to the mapped field data (or to the end of the section when the assembly has
    /// none). Null when the assembly needs no startup stub, or when the import table is not in
    /// the same section behind the embedded PDB.
    /// </summary>
    private static (int Start, int Length)? FindStubRegion(
        byte[] file, (int Start, int Length) section, (int Start, int Length) blob)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            var headers = peReader.PEHeaders;
            var sectionEnd = section.Start + section.Length;
            if (headers.PEHeader!.ImportTableDirectory.Size == 0 ||
                !headers.TryGetDirectoryOffset(headers.PEHeader.ImportTableDirectory, out var start) ||
                start < blob.Start + blob.Length || start >= sectionEnd)
            {
                return null;
            }

            var end = FindMappedFieldDataOffset(file, peReader, section, start) ?? sectionEnd;
            return end > start ? (start, end - start) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where the mapped static field data starts, taken from the lowest FieldRVA the metadata
    /// records. Null when the assembly maps no field data behind <paramref name="after"/>.
    /// </summary>
    private static int? FindMappedFieldDataOffset(
        byte[] file, PEReader peReader, (int Start, int Length) section, int after)
    {
        var headers = peReader.PEHeaders;
        var metadataReader = peReader.GetMetadataReader();
        var rows = metadataReader.GetTableRowCount(TableIndex.FieldRva);
        if (rows == 0)
        {
            return null;
        }

        var tableStart = headers.MetadataStartOffset + metadataReader.GetTableMetadataOffset(TableIndex.FieldRva);
        var rowSize = metadataReader.GetTableRowSize(TableIndex.FieldRva);
        int? lowest = null;
        for (var row = 0; row < rows; row++)
        {
            // FieldRVA rows start with the four-byte RVA of the mapped data.
            var rva = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                file.AsSpan(tableStart + row * rowSize));
            var index = headers.GetContainingSectionIndex(rva);
            if (index < 0)
            {
                continue;
            }
            var containing = headers.SectionHeaders[index];
            var offset = containing.PointerToRawData + (rva - containing.VirtualAddress);
            if (offset > after && offset < section.Start + section.Length)
            {
                lowest = lowest == null ? offset : Math.Min(lowest.Value, offset);
            }
        }
        return lowest;
    }

    private static (int Start, int Length)? FindSection(byte[] file, int offset)
    {
        try
        {
            using var peReader = new PEReader(System.Collections.Immutable.ImmutableArray.Create(file));
            foreach (var section in peReader.PEHeaders.SectionHeaders)
            {
                if (section.PointerToRawData <= offset &&
                    offset < section.PointerToRawData + section.SizeOfRawData &&
                    section.PointerToRawData + section.SizeOfRawData <= file.Length)
                {
                    return (section.PointerToRawData, section.SizeOfRawData);
                }
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// How many zero bytes the section ends with, counting no further back than
    /// <paramref name="floor"/> - the end of the last region that is being removed anyway, in
    /// front of which nothing can be padding.
    /// </summary>
    private static int TrailingZeros(byte[] file, (int Start, int Length) section, int floor)
    {
        var limit = Math.Max(section.Start, floor);
        var index = section.Start + section.Length;
        while (index > limit && file[index - 1] == 0)
        {
            index--;
        }
        return section.Start + section.Length - index;
    }

    private static byte[] Splice(byte[] file, List<(int Start, int Length)> spans)
    {
        var ordered = spans.OrderBy(s => s.Start).ToList();
        var result = new byte[file.Length - ordered.Sum(s => s.Length)];
        var read = 0;
        var write = 0;
        foreach (var (start, length) in ordered)
        {
            file.AsSpan(read, start - read).CopyTo(result.AsSpan(write));
            write += start - read;
            read = start + length;
        }
        file.AsSpan(read).CopyTo(result.AsSpan(write));
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
