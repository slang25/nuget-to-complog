using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Exceptions;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Service for reading PDB files and extracting metadata.
/// </summary>
public class PdbReaderService : IPdbReader
{
    private readonly PdbDiscoveryService _discoveryService;
    private readonly CompilationOptionsExtractor _compilationExtractor;
    private readonly IFileSystemService _fileSystem;

    public PdbReaderService(
        PdbDiscoveryService discoveryService,
        CompilationOptionsExtractor compilationExtractor,
        IFileSystemService fileSystem)
    {
        _discoveryService = discoveryService;
        _compilationExtractor = compilationExtractor;
        _fileSystem = fileSystem;
    }

    public async Task<string?> FindPdbAsync(string assemblyPath, string workingDirectory)
    {
        return await Task.FromResult(_discoveryService.FindPdbFile(assemblyPath, workingDirectory));
    }

    public bool HasEmbeddedPdb(string assemblyPath)
    {
        return _discoveryService.HasEmbeddedPdb(assemblyPath);
    }

    public async Task<PdbMetadata> ExtractMetadataAsync(
        string assemblyPath,
        string? pdbPath,
        bool hasReproducibleMarker,
        CancellationToken cancellationToken = default)
    {
        MetadataReader metadataReader;
        bool isEmbedded = false;

        if (pdbPath == null)
        {
            isEmbedded = true;
            metadataReader = GetEmbeddedPdbReader(assemblyPath);
        }
        else
        {
            metadataReader = await GetExternalPdbReaderAsync(pdbPath);
        }

        var compilationInfo = await _compilationExtractor.ExtractCompilationInfoAsync(
            metadataReader,
            _discoveryService.HasEmbeddedPdb(assemblyPath),
            hasReproducibleMarker,
            cancellationToken);

        var sourceFiles = ExtractSourceFiles(metadataReader);
        var sourceLinkJson = ExtractSourceLink(metadataReader);
        var embeddedResources = ExtractEmbeddedResources(assemblyPath);

        return new PdbMetadata(
            pdbPath,
            isEmbedded,
            compilationInfo.CompilerArguments,
            compilationInfo.MetadataReferences,
            sourceFiles,
            sourceLinkJson,
            embeddedResources);
    }

    private MetadataReader GetEmbeddedPdbReader(string assemblyPath)
    {
        var peStream = File.OpenRead(assemblyPath);
        var peReader = new PEReader(peStream);

        var embeddedPdb = peReader.ReadDebugDirectory()
            .First(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
        return pdbProvider.GetMetadataReader();
    }

    private async Task<MetadataReader> GetExternalPdbReaderAsync(string pdbPath)
    {
        var pdbStream = File.OpenRead(pdbPath);
        var metadataReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        return await Task.FromResult(metadataReaderProvider.GetMetadataReader());
    }

    // Document hash algorithm GUIDs per the Portable PDB specification.
    private static readonly Guid HashAlgorithmSha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");
    private static readonly Guid HashAlgorithmSha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");

    private List<SourceFileInfo> ExtractSourceFiles(MetadataReader metadataReader)
    {
        var sourceFiles = new List<SourceFileInfo>();

        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);

            var hash = document.Hash.IsNil ? null : metadataReader.GetBlobBytes(document.Hash);
            var algorithmGuid = document.HashAlgorithm.IsNil ? default : metadataReader.GetGuid(document.HashAlgorithm);
            string? hashAlgorithm =
                algorithmGuid == HashAlgorithmSha256 ? "sha256" :
                algorithmGuid == HashAlgorithmSha1 ? "sha1" :
                null;

            var embeddedSource = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .Where(cdi => metadataReader.GetGuid(cdi.Kind).ToString()
                    .Equals(CompilationOptionsExtractor.EmbeddedSourceGuid, StringComparison.OrdinalIgnoreCase))
                .Cast<CustomDebugInformation?>()
                .FirstOrDefault();

            bool isEmbedded = embeddedSource.HasValue && embeddedSource.Value.Kind != default;

            string? content = null;
            byte[]? contentBytes = null;
            if (isEmbedded && embeddedSource.HasValue)
            {
                var embeddedSourceBlob = metadataReader.GetBlobBytes(embeddedSource.Value.Value);
                contentBytes = DecompressEmbeddedSourceBytes(embeddedSourceBlob);
                content = contentBytes != null ? System.Text.Encoding.UTF8.GetString(contentBytes) : null;
            }

            sourceFiles.Add(new SourceFileInfo(name, content, isEmbedded, null, contentBytes, hash, hashAlgorithm));
        }

        return sourceFiles;
    }

    private string? ExtractSourceLink(MetadataReader metadataReader)
    {
        // Iterate the handles directly rather than using FirstOrDefault on the
        // projected struct: FirstOrDefault returns default(CustomDebugInformation)
        // when no Source Link CDI is present (e.g. MassTransit's embedded PDB), and
        // reading .Kind/.Value on that default value dereferences a null internal
        // MetadataReader, throwing a NullReferenceException.
        foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
            if (cdi.Kind.IsNil)
            {
                continue;
            }

            var guid = metadataReader.GetGuid(cdi.Kind);
            if (guid.ToString().Equals(CompilationOptionsExtractor.SourceLinkGuid, StringComparison.OrdinalIgnoreCase))
            {
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                return System.Text.Encoding.UTF8.GetString(blob);
            }
        }

        return null;
    }

    private byte[]? DecompressEmbeddedSourceBytes(byte[] blob)
    {
        if (blob.Length < 4)
        {
            return null;
        }

        try
        {
            var uncompressedSize = BitConverter.ToInt32(blob, 0);

            if (uncompressedSize == 0)
            {
                return blob[4..];
            }

            using var compressedStream = new MemoryStream(blob, 4, blob.Length - 4);
            using var deflateStream = new System.IO.Compression.DeflateStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream(uncompressedSize);

            deflateStream.CopyTo(decompressedStream);

            return decompressedStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private List<EmbeddedResourceInfo> ExtractEmbeddedResources(string assemblyPath)
    {
        var resources = new List<EmbeddedResourceInfo>();

        try
        {
            // Read the manifest resources from metadata rather than Assembly.LoadFrom: loading
            // a package's assembly into our own runtime can fail (or execute code) and a silent
            // failure here means the rebuild ships without its resources.
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            var metadataReader = peReader.GetMetadataReader();
            var resourcesDirectory = peReader.PEHeaders.CorHeader?.ResourcesDirectory;
            if (resourcesDirectory is not { RelativeVirtualAddress: > 0 } directory)
            {
                return resources;
            }

            foreach (var handle in metadataReader.ManifestResources)
            {
                var resource = metadataReader.GetManifestResource(handle);
                if (!resource.Implementation.IsNil)
                {
                    continue; // lives in another file/assembly, not embedded here
                }

                var name = metadataReader.GetString(resource.Name);
                var data = peReader.GetSectionData(directory.RelativeVirtualAddress + (int)resource.Offset);
                var blobReader = data.GetReader();
                var length = blobReader.ReadInt32();
                var content = blobReader.ReadBytes(length);

                resources.Add(new EmbeddedResourceInfo(name, content, content.Length));
            }
        }
        catch (Exception)
        {
            // Unreadable/invalid assembly: return what we have.
        }

        return resources;
    }
}
