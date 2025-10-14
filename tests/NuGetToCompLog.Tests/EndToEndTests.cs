using NuGetToCompLog;
using NuGetToCompLog.Commands;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Serilog_4_3_0_ShouldExtractAllSourceFiles()
    {
        // Clean up any pre-existing Serilog artifacts
        var existingDir = Directory.GetDirectories(Directory.GetCurrentDirectory())
            .FirstOrDefault(d => d.Contains("Serilog-4.3.0-complog"));
        if (existingDir != null && Directory.Exists(existingDir))
        {
            Directory.Delete(existingDir, true);
        }
        
        var existingFile = Path.Combine(Directory.GetCurrentDirectory(), "Serilog.4.3.0.complog");
        if (File.Exists(existingFile))
        {
            File.Delete(existingFile);
        }

        // Arrange & Act - Run the full extraction process
        // This will create its own temp directory
        var services = new ServiceCollection();
        services.AddNuGetToCompLogServices();
        await using var serviceProvider = services.BuildServiceProvider();
        
        var handler = serviceProvider.GetRequiredService<ProcessPackageCommandHandler>();
        var command = new ProcessPackageCommand("Serilog", "4.3.0");
        var result = await handler.HandleAsync(command, CancellationToken.None);
        
        // Ensure the handler completed successfully
        Assert.NotNull(result);

        // The tool will have created a complog structure directory
        // Find it in the current directory
        var complogStructureDir = Directory.GetDirectories(Directory.GetCurrentDirectory())
            .FirstOrDefault(d => d.Contains("Serilog-4.3.0-complog"));

        try
        {
            Assert.NotNull(complogStructureDir);
            Assert.True(Directory.Exists(complogStructureDir), "CompLog structure directory should exist");

            // Assert - Check that source files were extracted
            var sourcesDir = Path.Combine(complogStructureDir, "sources");
            Assert.True(Directory.Exists(sourcesDir), "Sources directory should exist");

            var sourceFiles = Directory.GetFiles(sourcesDir, "*.cs", SearchOption.AllDirectories);
            
            // The PDB indicates there are 117 source files, but only 5 are embedded
            // The rest should be downloaded from Source Link
            // We should have significantly more than just the 5 embedded files
            Assert.True(sourceFiles.Length > 100, 
                $"Expected approximately 117 source files, but found only {sourceFiles.Length}. " +
                "Source Link download may have failed.");

            // Check compiler arguments file
            var compilerArgsFile = Path.Combine(complogStructureDir, "compiler-arguments.txt");
            Assert.True(File.Exists(compilerArgsFile), "Compiler arguments file should exist");
            
            var compilerArgs = await File.ReadAllTextAsync(compilerArgsFile);
            Assert.Contains("source-file-count", compilerArgs);
            Assert.Contains("117", compilerArgs);

            // Check that embedded resources were extracted
            var resourcesDir = Path.Combine(complogStructureDir, "resources");
            Assert.True(Directory.Exists(resourcesDir), "Resources directory should exist");
            
            // Serilog 4.3.0 has one embedded resource: ILLink.Substitutions.xml
            var resourceFiles = Directory.GetFiles(resourcesDir, "*.*", SearchOption.AllDirectories);
            Assert.NotEmpty(resourceFiles);
            
            var ilLinkFile = resourceFiles.FirstOrDefault(f => Path.GetFileName(f) == "ILLink.Substitutions.xml");
            Assert.NotNull(ilLinkFile);
            Assert.True(File.Exists(ilLinkFile), "ILLink.Substitutions.xml should be extracted");
            
            var ilLinkContent = await File.ReadAllTextAsync(ilLinkFile);
            Assert.NotEmpty(ilLinkContent);
            Assert.Contains("<linker>", ilLinkContent); // XML file should contain linker element
            Assert.Contains("Serilog", ilLinkContent); // Should reference Serilog assembly

            // Verify the complog file was created
            var complogFile = Path.Combine(Directory.GetCurrentDirectory(), "Serilog.4.3.0.complog");
            Assert.True(File.Exists(complogFile), "CompLog file should be created");
            
            // The complog should be much larger if it contains all sources
            var fileInfo = new FileInfo(complogFile);
            // A complete complog with 117 source files should be significantly larger
            // than one with only 5 embedded files (which was ~1.95MB)
            // With all sources it should be at least 2MB
            Assert.True(fileInfo.Length > 2_000_000, 
                $"CompLog file seems too small ({fileInfo.Length:N0} bytes). " +
                "Expected >2MB with all source files. May be missing source files.");
        }
        finally
        {
            // Cleanup
            if (complogStructureDir != null && Directory.Exists(complogStructureDir))
            {
                Directory.Delete(complogStructureDir, true);
            }
            
            var complogFile = Path.Combine(Directory.GetCurrentDirectory(), "Serilog.4.3.0.complog");
            if (File.Exists(complogFile))
            {
                File.Delete(complogFile);
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
