using NuGetToCompLog;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Tests;

/// <summary>
/// End-to-end tests that download and process real NuGet packages.
/// These tests verify the complete workflow works with actual packages.
/// </summary>
public class EndToEndTests
{
    [Fact(Skip = "Integration test - requires network access")]
    public async Task CanExtractMetadataFromRealPackage()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Download a known package with embedded PDB (e.g., Newtonsoft.Json)
            var packagePath = await DownloadPackageAsync("Newtonsoft.Json", "13.0.3", tempDir);
            var extractPath = Path.Combine(tempDir, "extracted");
            await ZipFile.ExtractToDirectoryAsync(packagePath, extractPath);

            // Find the .NET 6.0 assembly (should have embedded PDB)
            var dllPath = Path.Combine(extractPath, "lib", "net6.0", "Newtonsoft.Json.dll");
            
            if (!File.Exists(dllPath))
            {
                // Try another TFM
                var libDir = Path.Combine(extractPath, "lib");
                dllPath = Directory.GetFiles(libDir, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
            }

            Assert.NotNull(dllPath);
            Assert.True(File.Exists(dllPath));

            // Act - Extract metadata references from the PDB
            await using var peStream = File.OpenRead(dllPath);
            using var peReader = new PEReader(peStream);
            
            var embeddedPdb = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            if (embeddedPdb.DataSize > 0)
            {
                var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
                var metadataReader = pdbProvider.GetMetadataReader();

                // Find the metadata references custom debug info
                foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
                {
                    var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
                    var guid = metadataReader.GetGuid(cdi.Kind);

                    // CompilationMetadataReferences GUID
                    if (guid.ToString().Equals("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D", 
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var blob = metadataReader.GetBlobBytes(cdi.Value);
                        
                        // This should not throw "cannot read beyond end of stream"
                        var references = MetadataReferenceParser.Parse(blob);
                        
                        // Assert
                        Assert.NotEmpty(references);
                        Assert.All(references, r => 
                        {
                            Assert.NotNull(r.FileName);
                            Assert.NotEmpty(r.FileName);
                        });
                    }
                }
            }
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static async Task<string> DownloadPackageAsync(string packageId, string versionString, string outputDir)
    {
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
        var version = NuGetVersion.Parse(versionString);
        var packagePath = Path.Combine(outputDir, $"{packageId}.{version}.nupkg");

        await using var packageStream = File.Create(packagePath);
        var success = await resource.CopyNupkgToStreamAsync(
            packageId,
            version,
            packageStream,
            cache,
            NullLogger.Instance,
            CancellationToken.None);

        if (!success)
        {
            throw new Exception($"Failed to download package {packageId} {version}");
        }

        return packagePath;
    }
}
