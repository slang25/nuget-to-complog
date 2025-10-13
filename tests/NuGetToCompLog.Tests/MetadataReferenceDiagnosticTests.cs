using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO.Compression;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGetToCompLog.Tests;

/// <summary>
/// Diagnostic test to examine the actual format of metadata references in real PDBs.
/// </summary>
public class MetadataReferenceDiagnosticTests
{
    [Fact]
    public async Task InspectRealMetadataReferenceFormat()
    {
        // Download and extract Newtonsoft.Json
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var packagePath = await DownloadPackageAsync("Newtonsoft.Json", "13.0.3", tempDir);
            var extractPath = Path.Combine(tempDir, "extracted");
            await ZipFile.ExtractToDirectoryAsync(packagePath, extractPath);

            // Download symbols
            var snupkgPath = await DownloadSymbolsPackageAsync("Newtonsoft.Json", "13.0.3", tempDir);
            if (snupkgPath != null)
            {
                var symbolsPath = Path.Combine(tempDir, "symbols");
                await ZipFile.ExtractToDirectoryAsync(snupkgPath, symbolsPath);

                // Find a PDB
                var pdbFiles = Directory.GetFiles(symbolsPath, "*.pdb", SearchOption.AllDirectories);
                if (pdbFiles.Any())
                {
                    await using var pdbStream = File.OpenRead(pdbFiles[0]);
                    using var metadataReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                    var metadataReader = metadataReaderProvider.GetMetadataReader();

                    foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
                    {
                        var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
                        var guid = metadataReader.GetGuid(cdi.Kind);

                        // CompilationMetadataReferences GUID
                        if (guid.ToString().Equals("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            var blob = metadataReader.GetBlobBytes(cdi.Value);
                            
                            // Print hex dump
                            Console.WriteLine($"Blob length: {blob.Length}");
                            Console.WriteLine("First 200 bytes (hex):");
                            for (int i = 0; i < Math.Min(200, blob.Length); i += 16)
                            {
                                Console.Write($"{i:X4}: ");
                                for (int j = 0; j < 16 && i + j < blob.Length; j++)
                                {
                                    Console.Write($"{blob[i + j]:X2} ");
                                }
                                Console.Write("  ");
                                for (int j = 0; j < 16 && i + j < blob.Length; j++)
                                {
                                    var b = blob[i + j];
                                    Console.Write(b is >= 32 and < 127 ? (char)b : '.');
                                }
                                Console.WriteLine();
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private async Task<string> DownloadPackageAsync(string packageId, string versionString, string outputDir)
    {
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
        var version = NuGetVersion.Parse(versionString);
        var packagePath = Path.Combine(outputDir, $"{packageId}.{version}.nupkg");

        await using var packageStream = File.Create(packagePath);
        await resource.CopyNupkgToStreamAsync(packageId, version, packageStream, cache,
            NullLogger.Instance, CancellationToken.None);

        return packagePath;
    }

    private async Task<string?> DownloadSymbolsPackageAsync(string packageId, string versionString, string outputDir)
    {
        var version = NuGetVersion.Parse(versionString);
        var snupkgPath = Path.Combine(outputDir, $"{packageId}.{version}.snupkg");
        
        var snupkgUrls = new[]
        {
            $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToNormalizedString()}/{packageId.ToLowerInvariant()}.{version.ToNormalizedString()}.snupkg",
        };

        using var httpClient = new HttpClient();
        
        foreach (var url in snupkgUrls)
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    await using var fileStream = File.Create(snupkgPath);
                    await response.Content.CopyToAsync(fileStream);
                    return snupkgPath;
                }
            }
            catch { }
        }

        return null;
    }
}
