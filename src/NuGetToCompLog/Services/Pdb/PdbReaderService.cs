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

    private List<SourceFileInfo> ExtractSourceFiles(MetadataReader metadataReader)
    {
        var sourceFiles = new List<SourceFileInfo>();

        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);

            var embeddedSource = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .Where(cdi => metadataReader.GetGuid(cdi.Kind).ToString()
                    .Equals(CompilationOptionsExtractor.EmbeddedSourceGuid, StringComparison.OrdinalIgnoreCase))
                .Cast<CustomDebugInformation?>()
                .FirstOrDefault();

            bool isEmbedded = embeddedSource.HasValue && embeddedSource.Value.Kind != default;

            string? content = null;
            if (isEmbedded && embeddedSource.HasValue)
            {
                var embeddedSourceBlob = metadataReader.GetBlobBytes(embeddedSource.Value.Value);
                content = DecompressEmbeddedSource(embeddedSourceBlob);
            }

            sourceFiles.Add(new SourceFileInfo(name, content, isEmbedded, null));
        }

        return sourceFiles;
    }

    private string? ExtractSourceLink(MetadataReader metadataReader)
    {
        var sourceLinkHandle = metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
            .Select(h => metadataReader.GetCustomDebugInformation(h))
            .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString()
                .Equals(CompilationOptionsExtractor.SourceLinkGuid, StringComparison.OrdinalIgnoreCase));

        if (sourceLinkHandle.Kind != default)
        {
            var blob = metadataReader.GetBlobBytes(sourceLinkHandle.Value);
            return System.Text.Encoding.UTF8.GetString(blob);
        }

        return null;
    }

    private string? DecompressEmbeddedSource(byte[] blob)
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
                return System.Text.Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
            }

            using var compressedStream = new MemoryStream(blob, 4, blob.Length - 4);
            using var deflateStream = new System.IO.Compression.DeflateStream(compressedStream, System.IO.Compression.CompressionMode.Decompress);
            using var decompressedStream = new MemoryStream(uncompressedSize);

            deflateStream.CopyTo(decompressedStream);

            return System.Text.Encoding.UTF8.GetString(decompressedStream.ToArray());
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
            var assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);
            var resourceNames = assembly.GetManifestResourceNames();

            foreach (var resourceName in resourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var memoryStream = new MemoryStream();
                    stream.CopyTo(memoryStream);
                    var content = memoryStream.ToArray();
                    
                    resources.Add(new EmbeddedResourceInfo(
                        resourceName,
                        content,
                        content.Length));
                }
            }
        }
        catch (Exception)
        {
            // If we can't load the assembly or extract resources, just return empty list
            // This can happen with invalid assemblies or platform-specific assemblies
        }

        return resources;
    }
}
