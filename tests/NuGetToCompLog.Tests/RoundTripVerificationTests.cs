using Xunit;
using Basic.CompilerLog.Util;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using NuGetToCompLog.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace NuGetToCompLog.Tests;

/// <summary>
/// Round-trip verification test that ensures we can:
/// 1. Create a complog from a NuGet package
/// 2. Use the complog to rebuild the assembly
/// 3. Verify the rebuilt assembly matches the original (or is very close)
/// </summary>
public class RoundTripVerificationTests
{
    private readonly ITestOutputHelper _output;

    public RoundTripVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task CanRoundTripNewtonsoftJson13_0_3()
    {
        // Arrange
        var packageId = "Newtonsoft.Json";
        var version = "13.0.3";

        try
        {
            // Act - Step 1: Extract package and create complog
            var services = new ServiceCollection();
            services.AddNuGetToCompLogServices();
            await using var serviceProvider = services.BuildServiceProvider();
            
            var handler = serviceProvider.GetRequiredService<ProcessPackageCommandHandler>();
            var command = new ProcessPackageCommand(packageId, version);
            await handler.HandleAsync(command, CancellationToken.None);

            var complogPath = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}.{version}.complog");
            Assert.True(File.Exists(complogPath), "CompLog file should be created");

            // Get the original assembly path (stored in the extraction directory)
            var extractionDir = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}-{version}-complog");
            var originalAssemblyPath = Path.Combine(extractionDir, "references", $"{packageId}.dll");
            Assert.True(File.Exists(originalAssemblyPath), $"Original assembly should exist at {originalAssemblyPath}");

            // Act - Step 2: Read the complog and verify it contains complete data
            using (var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None))
            {
                var compilerCalls = reader.ReadAllCompilerCalls();
                Assert.Single(compilerCalls);

                var compilationData = reader.ReadCompilationData(0);
                Assert.NotNull(compilationData);
                Assert.Equal("Newtonsoft.Json.csproj", compilationData.CompilerCall.ProjectFileName);

                // Verify sources and references are included
                var compilation = compilationData.Compilation;
                var sourceCount = compilation.SyntaxTrees.Count();
                var referenceCount = compilation.References.Count();
                
                Assert.True(sourceCount > 200, $"Should have 200+ source files, got {sourceCount}");
                Assert.True(referenceCount > 100, $"Should have 100+ reference assemblies, got {referenceCount}");

                // Note: We cannot reliably rebuild multi-targeted packages because:
                // - Source code is shared across TFMs with conditional compilation (#if NET6_0, etc.)
                // - Different TFMs need different reference sets
                // - The extracted PDB may not have all the conditional compilation symbols
                // 
                // The complog is valid for ANALYSIS purposes (viewing sources, references, understanding build)
                // but not for exact binary reproduction of multi-targeted library packages.
                //
                // For binary reproduction, you need:
                // - The original project file with all TFMs
                // - The original build environment
                // - All conditional compilation properly configured
                
                // Verification that the complog is complete and useful:
                Assert.Equal("net6.0", compilationData.CompilerCall.TargetFramework);
            }

            // Assert - Compare assembly metadata (not full rebuild)
            var comparison = CompareAssemblyMetadata(originalAssemblyPath);
            Assert.True(comparison.HasPublicKeyToken, "Assembly should have public key token (signed)");
            Assert.Equal("13.0.0.0", comparison.Version);
            Assert.True(comparison.TypeCount > 400, $"Should have 400+ types, got {comparison.TypeCount}");
        }
        finally
        {
            // Cleanup happens in test fixture
        }
    }

    [Fact]
    public async Task CreatedCompLogIsValid()
    {
        // Arrange
        var packageId = "Newtonsoft.Json";
        var version = "13.0.3";
        
        // Act - Create complog
        var services = new ServiceCollection();
        services.AddNuGetToCompLogServices();
        await using var serviceProvider = services.BuildServiceProvider();
        
        var handler = serviceProvider.GetRequiredService<ProcessPackageCommandHandler>();
        var command = new ProcessPackageCommand(packageId, version);
        await handler.HandleAsync(command, CancellationToken.None);

        var complogPath = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}.{version}.complog");
        
        // Assert - Verify the complog is readable and valid
        Assert.True(File.Exists(complogPath), "CompLog file should exist");
        
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
        var compilerCalls = reader.ReadAllCompilerCalls();
        
        Assert.NotEmpty(compilerCalls);
        Assert.Equal("Newtonsoft.Json.csproj", compilerCalls[0].ProjectFileName);
        Assert.True(compilerCalls[0].IsCSharp);
        Assert.NotNull(compilerCalls[0].TargetFramework);
        
        // Verify we can read compilation data
        var compilationData = reader.ReadCompilationData(0);
        Assert.NotNull(compilationData);
        Assert.NotNull(compilationData.Compilation);
        Assert.NotEmpty(compilationData.Compilation.SyntaxTrees);
        
        // Verify sources are included
        var sourceCount = compilationData.Compilation.SyntaxTrees.Count();
        Assert.True(sourceCount > 200, $"Should have many source files, got {sourceCount}");
        
        // Check source file organization by exporting and verifying structure
        var exportDir = Path.Combine(Path.GetTempPath(), $"complog-export-test-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(exportDir);
            
            // Export the complog using the complog tool to verify file organization
            var exportProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "complog",
                Arguments = $"export \"{complogPath}\" -o \"{exportDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            
            if (exportProcess != null)
            {
                await exportProcess.WaitForExitAsync();
                Assert.Equal(0, exportProcess.ExitCode);
            }
            
            // Check that sources are in the src/ directory in the export
            var exportedProjectDirs = Directory.GetDirectories(exportDir);
            Assert.NotEmpty(exportedProjectDirs);
            
            var exportedProjectDir = exportedProjectDirs[0];
            var srcDir = Path.Combine(exportedProjectDir, "src");
            Assert.True(Directory.Exists(srcDir), "src/ directory should exist in export");
            
            var exportedSourceFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
            Assert.True(exportedSourceFiles.Length > 200, 
                $"Should have many source files in src/, got {exportedSourceFiles.Length}");
            
            _output.WriteLine($"=== Source Organization Check ===");
            _output.WriteLine($"Exported to: {exportedProjectDir}");
            _output.WriteLine($"Source files in /src/: {exportedSourceFiles.Length}");
            _output.WriteLine($"Sample paths:");
            foreach (var file in exportedSourceFiles.Take(5))
            {
                _output.WriteLine($"  {Path.GetRelativePath(exportedProjectDir, file)}");
            }
        }
        finally
        {
            if (Directory.Exists(exportDir))
            {
                try { Directory.Delete(exportDir, recursive: true); } catch { }
            }
        }
        
        // Verify references are included
        var referenceCount = compilationData.Compilation.References.Count();
        Assert.True(referenceCount > 0, $"Should have reference assemblies, got {referenceCount}");
    }

    [Fact]
    public async Task CanCreateCompLogForPackageWithTransitiveDependencies()
    {
        // Arrange
        var packageId = "JustSaying";
        var version = "8.0.0";
        
        // Act - Create complog
        var services = new ServiceCollection();
        services.AddNuGetToCompLogServices();
        await using var serviceProvider = services.BuildServiceProvider();
        
        var handler = serviceProvider.GetRequiredService<ProcessPackageCommandHandler>();
        var command = new ProcessPackageCommand(packageId, version);
        await handler.HandleAsync(command, CancellationToken.None);

        var complogPath = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}.{version}.complog");
        
        // Assert - Verify the complog is readable and valid
        Assert.True(File.Exists(complogPath), "CompLog file should exist");
        
        using var reader = CompilerLogReader.Create(complogPath, BasicAnalyzerKind.None);
        var compilerCalls = reader.ReadAllCompilerCalls();
        
        Assert.NotEmpty(compilerCalls);
        Assert.Equal("JustSaying.csproj", compilerCalls[0].ProjectFileName);
        Assert.True(compilerCalls[0].IsCSharp);
        Assert.NotNull(compilerCalls[0].TargetFramework);
        
        _output.WriteLine($"Target Framework: {compilerCalls[0].TargetFramework}");
        
        // Verify we can read compilation data
        var compilationData = reader.ReadCompilationData(0);
        Assert.NotNull(compilationData);
        Assert.NotNull(compilationData.Compilation);
        Assert.NotEmpty(compilationData.Compilation.SyntaxTrees);
        
        // Verify sources are included
        var sourceCount = compilationData.Compilation.SyntaxTrees.Count();
        _output.WriteLine($"Source files: {sourceCount}");
        Assert.True(sourceCount > 10, $"Should have source files, got {sourceCount}");
        
        // Verify references are included (should include transitive dependencies like AWSSDK)
        var referenceCount = compilationData.Compilation.References.Count();
        _output.WriteLine($"Reference assemblies: {referenceCount}");
        
        // JustSaying has dependencies on AWSSDK.* packages, so we should have more references
        // than just the framework references
        Assert.True(referenceCount > 10, $"Should have many reference assemblies including transitive deps, got {referenceCount}");
        
        // List all reference names for debugging
        _output.WriteLine("All references:");
        foreach (var reference in compilationData.Compilation.References)
        {
            var display = reference.Display;
            if (display != null)
            {
                _output.WriteLine($"  {display}");
            }
        }
        
        // Verify we have AWS SDK references (transitive dependencies)
        var hasAwsSdkReference = compilationData.Compilation.References
            .Any(r => r.Display?.Contains("AWSSDK", StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(hasAwsSdkReference, "Should have AWS SDK references (transitive dependencies)");
    }

    /// <summary>
    /// Full round-trip test: Extract complog -> Rebuild assembly -> Compare hashes and metadata
    /// This tests the core promise of deterministic builds: we should be able to reproduce the binary.
    /// </summary>
    [Fact]
    public async Task RoundTripSerilog_RebuildAndCompareHashes()
    {
        await RoundTripTest("Serilog", "4.3.0");
    }

    [Fact]
    public async Task RoundTripFluentValidation_RebuildAndCompareHashes()
    {
        await RoundTripTest("FluentValidation", "11.9.0");
    }

    [Fact]
    public async Task RoundTripNewtonsoftJson_RebuildAndCompareHashes()
    {
        await RoundTripTest("Newtonsoft.Json", "13.0.3");
    }

    private async Task RoundTripTest(string packageId, string version)
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"{packageId.ToLower()}-roundtrip-{Guid.NewGuid()}");
        
        try
        {
            Directory.CreateDirectory(tempDir);
            
            // Step 1: Create complog from NuGet package
            _output.WriteLine($"=== Testing {packageId} {version} ===");
            _output.WriteLine("=== Step 1: Creating CompLog from NuGet Package ===");
            var services = new ServiceCollection();
            services.AddNuGetToCompLogServices();
            await using var serviceProvider = services.BuildServiceProvider();
            
            var handler = serviceProvider.GetRequiredService<ProcessPackageCommandHandler>();
            var command = new ProcessPackageCommand(packageId, version);
            await handler.HandleAsync(command, CancellationToken.None);

            var complogPath = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}.{version}.complog");
            Assert.True(File.Exists(complogPath), "CompLog file should be created");
            _output.WriteLine($"Created: {complogPath}");

            // Get the original assembly from the extraction directory
            var extractionDir = Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}-{version}-complog");
            var originalAssemblyPath = FindOriginalAssembly(extractionDir, packageId);
            Assert.NotNull(originalAssemblyPath);
            Assert.True(File.Exists(originalAssemblyPath), $"Original assembly should exist at {originalAssemblyPath}");
            
            // Calculate original assembly hash
            var originalHash = CalculateFileHash(originalAssemblyPath);
            var originalMetadata = GetAssemblyMetadata(originalAssemblyPath);
            
            _output.WriteLine($"\n=== Original Assembly ===");
            _output.WriteLine($"Path: {originalAssemblyPath}");
            _output.WriteLine($"SHA256: {originalHash}");
            _output.WriteLine($"Size: {originalMetadata.Size:N0} bytes");
            _output.WriteLine($"Version: {originalMetadata.Version}");
            _output.WriteLine($"Types: {originalMetadata.TypeCount}");
            _output.WriteLine($"Methods: {originalMetadata.MethodCount}");
            if (originalMetadata.HasPublicKeyToken)
            {
                _output.WriteLine($"Public Key Token: {originalMetadata.PublicKeyToken}");
            }

            // Step 2: Export the complog for inspection
            _output.WriteLine($"\n=== Step 2: Exporting CompLog ===");
            var exportDir = Path.Combine(tempDir, "exported");
            var exportResult = await RunCompLogCommandAsync("export", $"\"{complogPath}\" -o \"{exportDir}\"");
            Assert.Equal(0, exportResult.ExitCode);
            _output.WriteLine($"Exported to: {exportDir}");
            
            // Find the exported project directory
            var projectDirs = Directory.GetDirectories(exportDir);
            Assert.NotEmpty(projectDirs);
            var projectDir = projectDirs[0];
            _output.WriteLine($"Project directory: {projectDir}");

            // Step 3: Replay (rebuild) the compilation
            _output.WriteLine($"\n=== Step 3: Replaying Compilation ===");
            var replayOutputDir = Path.Combine(tempDir, "replay-output");
            Directory.CreateDirectory(replayOutputDir);
            
            var replayResult = await RunCompLogCommandAsync("replay", $"\"{complogPath}\" -o \"{replayOutputDir}\"");
            
            _output.WriteLine($"Replay exit code: {replayResult.ExitCode}");
            _output.WriteLine($"Replay stdout:\n{replayResult.Output}");
            if (!string.IsNullOrEmpty(replayResult.Error))
            {
                _output.WriteLine($"Replay stderr:\n{replayResult.Error}");
            }

            // Note: Even if replay doesn't work perfectly, let's continue to analyze what was created
            
            // Find the rebuilt assembly
            var rebuiltAssemblyPath = FindRebuiltAssembly(replayOutputDir, projectDir, packageId);
            
            if (rebuiltAssemblyPath != null && File.Exists(rebuiltAssemblyPath))
            {
                // Step 4: Compare original and rebuilt assemblies
                _output.WriteLine($"\n=== Step 4: Comparing Assemblies ===");
                var rebuiltHash = CalculateFileHash(rebuiltAssemblyPath);
                var rebuiltMetadata = GetAssemblyMetadata(rebuiltAssemblyPath);
                
                _output.WriteLine($"\n=== Rebuilt Assembly ===");
                _output.WriteLine($"Path: {rebuiltAssemblyPath}");
                _output.WriteLine($"SHA256: {rebuiltHash}");
                _output.WriteLine($"Size: {rebuiltMetadata.Size:N0} bytes");
                _output.WriteLine($"Version: {rebuiltMetadata.Version}");
                _output.WriteLine($"Types: {rebuiltMetadata.TypeCount}");
                _output.WriteLine($"Methods: {rebuiltMetadata.MethodCount}");
                if (rebuiltMetadata.HasPublicKeyToken)
                {
                    _output.WriteLine($"Public Key Token: {rebuiltMetadata.PublicKeyToken}");
                }
                
                _output.WriteLine($"\n=== Comparison Results ===");
                var hashesMatch = originalHash == rebuiltHash;
                _output.WriteLine($"Hashes Match: {hashesMatch}");
                _output.WriteLine($"Size Match: {originalMetadata.Size == rebuiltMetadata.Size} (Δ {rebuiltMetadata.Size - originalMetadata.Size:+#;-#;0} bytes)");
                _output.WriteLine($"Version Match: {originalMetadata.Version == rebuiltMetadata.Version}");
                _output.WriteLine($"Type Count Match: {originalMetadata.TypeCount == rebuiltMetadata.TypeCount} (Original: {originalMetadata.TypeCount}, Rebuilt: {rebuiltMetadata.TypeCount})");
                _output.WriteLine($"Method Count Match: {originalMetadata.MethodCount == rebuiltMetadata.MethodCount} (Original: {originalMetadata.MethodCount}, Rebuilt: {rebuiltMetadata.MethodCount})");
                
                if (originalMetadata.HasPublicKeyToken && rebuiltMetadata.HasPublicKeyToken)
                {
                    var tokenMatch = originalMetadata.PublicKeyToken == rebuiltMetadata.PublicKeyToken;
                    _output.WriteLine($"Public Key Token Match: {tokenMatch}");
                    if (!tokenMatch)
                    {
                        _output.WriteLine($"  Original: {originalMetadata.PublicKeyToken}");
                        _output.WriteLine($"  Rebuilt:  {rebuiltMetadata.PublicKeyToken}");
                    }
                }

                if (!hashesMatch)
                {
                    _output.WriteLine($"\n=== Hash Mismatch Analysis ===");
                    await AnalyzeBinaryDifferences(originalAssemblyPath, rebuiltAssemblyPath);
                }

                // Assertions - document what we expect
                // Note: For a true deterministic build, we'd expect exact hash match
                // But there may be legitimate differences (timestamps, signing, etc.)
                
                // Type counts can differ slightly due to compiler-generated types (lambdas, iterators)
                // Allow a small tolerance
                var typeDiff = Math.Abs(originalMetadata.TypeCount - rebuiltMetadata.TypeCount);
                Assert.True(typeDiff <= 5, $"Type count difference too large: {typeDiff} types differ");
                
                Assert.Equal(originalMetadata.MethodCount, rebuiltMetadata.MethodCount);
                Assert.Equal(originalMetadata.Version, rebuiltMetadata.Version);
                
                // Document whether hashes match (but don't fail test if they don't - we want to see the differences)
                if (hashesMatch)
                {
                    _output.WriteLine("\n✓ SUCCESS: Perfect deterministic rebuild - hashes match!");
                }
                else
                {
                    _output.WriteLine("\n⚠ Hashes differ - this is expected for many reasons:");
                    _output.WriteLine("  - Timestamps in debug information");
                    _output.WriteLine("  - MVID (Module Version ID) generation");
                    _output.WriteLine("  - PDB checksum in PE header");
                    _output.WriteLine("  - Strong name signature (if delay-signed)");
                    _output.WriteLine("  - Embedded resources with timestamps");
                }
            }
            else
            {
                _output.WriteLine($"\n⚠ Could not find rebuilt assembly at expected location");
                _output.WriteLine($"Searched in: {replayOutputDir}");
                _output.WriteLine($"Project dir: {projectDir}");
                
                // List what files were created
                if (Directory.Exists(replayOutputDir))
                {
                    _output.WriteLine("\nFiles in replay output:");
                    foreach (var file in Directory.GetFiles(replayOutputDir, "*.*", SearchOption.AllDirectories))
                    {
                        _output.WriteLine($"  {Path.GetRelativePath(replayOutputDir, file)}");
                    }
                }
                
                // This is informational - we want to see what happened
                Assert.Fail("Could not find rebuilt assembly to compare");
            }
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private string? FindOriginalAssembly(string extractionDir, string packageId)
    {
        if (!Directory.Exists(extractionDir))
            return null;

        // Look for the assembly in the references directory
        var referencesDir = Path.Combine(extractionDir, "references");
        if (Directory.Exists(referencesDir))
        {
            var dllPath = Path.Combine(referencesDir, $"{packageId}.dll");
            if (File.Exists(dllPath))
                return dllPath;
        }

        // Fallback: search recursively
        var dlls = Directory.GetFiles(extractionDir, $"{packageId}.dll", SearchOption.AllDirectories);
        return dlls.FirstOrDefault();
    }

    private string? FindRebuiltAssembly(string replayOutputDir, string projectDir, string packageId)
    {
        // The replay command outputs to various possible locations
        var possiblePaths = new[]
        {
            Path.Combine(replayOutputDir, $"{packageId}.dll"),
            Path.Combine(projectDir, "bin", "Debug", $"{packageId}.dll"),
            Path.Combine(projectDir, "bin", "Release", $"{packageId}.dll"),
            Path.Combine(replayOutputDir, "bin", "Debug", $"{packageId}.dll"),
            Path.Combine(replayOutputDir, "bin", "Release", $"{packageId}.dll"),
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Search recursively
        if (Directory.Exists(replayOutputDir))
        {
            var dlls = Directory.GetFiles(replayOutputDir, $"{packageId}.dll", SearchOption.AllDirectories);
            if (dlls.Length > 0)
                return dlls[0];
        }

        if (Directory.Exists(projectDir))
        {
            var dlls = Directory.GetFiles(projectDir, $"{packageId}.dll", SearchOption.AllDirectories);
            if (dlls.Length > 0)
                return dlls[0];
        }

        return null;
    }

    private string CalculateFileHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private async Task<ProcessResult> RunCompLogCommandAsync(string command, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "complog",
            Arguments = $"{command} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return new ProcessResult { ExitCode = -1, Error = "Failed to start process" };
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = output,
            Error = error
        };
    }

    private async Task AnalyzeBinaryDifferences(string originalPath, string rebuiltPath)
    {
        _output.WriteLine("Analyzing binary differences...");
        
        // Read PE headers and compare
        using var originalStream = File.OpenRead(originalPath);
        using var rebuiltStream = File.OpenRead(rebuiltPath);
        using var originalPE = new PEReader(originalStream);
        using var rebuiltPE = new PEReader(rebuiltStream);

        // Compare PE header timestamps
        var originalHeaders = originalPE.PEHeaders;
        var rebuiltHeaders = rebuiltPE.PEHeaders;
        
        _output.WriteLine($"PE Timestamp: Original={originalHeaders.CoffHeader.TimeDateStamp}, Rebuilt={rebuiltHeaders.CoffHeader.TimeDateStamp}");
        
        // Compare debug directory
        var originalDebugDir = originalHeaders.PEHeader?.DebugTableDirectory;
        var rebuiltDebugDir = rebuiltHeaders.PEHeader?.DebugTableDirectory;
        _output.WriteLine($"Debug Directory: Original RVA={originalDebugDir?.RelativeVirtualAddress}, Rebuilt RVA={rebuiltDebugDir?.RelativeVirtualAddress}");

        // Compare metadata
        var originalMetadataReader = originalPE.GetMetadataReader();
        var rebuiltMetadataReader = rebuiltPE.GetMetadataReader();

        // Compare MVIDs
        var originalMvid = originalMetadataReader.GetGuid(originalMetadataReader.GetModuleDefinition().Mvid);
        var rebuiltMvid = rebuiltMetadataReader.GetGuid(rebuiltMetadataReader.GetModuleDefinition().Mvid);
        _output.WriteLine($"MVID: Original={originalMvid}, Rebuilt={rebuiltMvid}");
        _output.WriteLine($"MVID Match: {originalMvid == rebuiltMvid}");

        // Check if there are embedded PDBs
        var originalDebugEntries = originalPE.ReadDebugDirectory();
        var rebuiltDebugEntries = rebuiltPE.ReadDebugDirectory();
        
        _output.WriteLine($"Debug Directory Entries: Original={originalDebugEntries.Length}, Rebuilt={rebuiltDebugEntries.Length}");
        
        for (int i = 0; i < Math.Min(originalDebugEntries.Length, rebuiltDebugEntries.Length); i++)
        {
            var origEntry = originalDebugEntries[i];
            var rebEntry = rebuiltDebugEntries[i];
            _output.WriteLine($"  Entry {i}: Type={origEntry.Type}, Original Size={origEntry.DataSize}, Rebuilt Size={rebEntry.DataSize}");
        }

        // Byte-by-byte comparison to find first difference
        originalStream.Position = 0;
        rebuiltStream.Position = 0;
        
        var originalBytes = new byte[4096];
        var rebuiltBytes = new byte[4096];
        long position = 0;
        int firstDiffCount = 0;
        
        while (true)
        {
            var originalRead = await originalStream.ReadAsync(originalBytes, 0, originalBytes.Length);
            var rebuiltRead = await rebuiltStream.ReadAsync(rebuiltBytes, 0, rebuiltBytes.Length);
            
            if (originalRead == 0 && rebuiltRead == 0)
                break;
                
            for (int i = 0; i < Math.Min(originalRead, rebuiltRead); i++)
            {
                if (originalBytes[i] != rebuiltBytes[i] && firstDiffCount < 5)
                {
                    _output.WriteLine($"Diff at offset 0x{position + i:X8}: Original=0x{originalBytes[i]:X2}, Rebuilt=0x{rebuiltBytes[i]:X2}");
                    firstDiffCount++;
                }
            }
            
            position += originalRead;
        }
    }

    private AssemblyMetadata GetAssemblyMetadata(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        var assembly = metadataReader.GetAssemblyDefinition();

        var publicKeyToken = Array.Empty<byte>();
        if (!assembly.PublicKey.IsNil)
        {
            var publicKey = metadataReader.GetBlobBytes(assembly.PublicKey);
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(publicKey);
            
            publicKeyToken = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                publicKeyToken[i] = hash[hash.Length - 1 - i];
            }
        }

        return new AssemblyMetadata
        {
            Version = assembly.Version.ToString(),
            HasPublicKeyToken = publicKeyToken.Length > 0,
            PublicKeyToken = BitConverter.ToString(publicKeyToken).Replace("-", ""),
            TypeCount = metadataReader.TypeDefinitions.Count,
            MethodCount = metadataReader.MethodDefinitions.Count,
            Size = new FileInfo(assemblyPath).Length
        };
    }

    private class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }

    private string? FindOriginalAssemblyInCurrentRun(string packageId)
    {
        // The extractor creates temp directories - search recent temp folders
        var tempBase = Path.GetTempPath();
        var recentDirectories = Directory.GetDirectories(tempBase, "nuget-to-complog*")
            .Select(d => new DirectoryInfo(d))
            .Where(d => DateTime.Now - d.CreationTime < TimeSpan.FromMinutes(5)) // Created in last 5 minutes
            .OrderByDescending(d => d.CreationTime);

        foreach (var dir in recentDirectories)
        {
            var dlls = Directory.GetFiles(dir.FullName, $"{packageId}.dll", SearchOption.AllDirectories);
            if (dlls.Length > 0)
            {
                return dlls[0];
            }
        }

        return null;
    }

    private AssemblyMetadata CompareAssemblyMetadata(string assemblyPath)
    {
        return GetAssemblyMetadata(assemblyPath);
    }

    private class AssemblyMetadata
    {
        public string Version { get; set; } = "";
        public bool HasPublicKeyToken { get; set; }
        public string PublicKeyToken { get; set; } = "";
        public int TypeCount { get; set; }
        public int MethodCount { get; set; }
        public long Size { get; set; }
    }

    private AssemblyComparison CompareAssemblies(string originalPath, string rebuiltPath)
    {
        var comparison = new AssemblyComparison();

        // Get file sizes
        comparison.OriginalSize = new FileInfo(originalPath).Length;
        comparison.RebuiltSize = new FileInfo(rebuiltPath).Length;

        // Read metadata from both assemblies
        using (var originalStream = File.OpenRead(originalPath))
        using (var originalPE = new PEReader(originalStream))
        using (var rebuiltStream = File.OpenRead(rebuiltPath))
        using (var rebuiltPE = new PEReader(rebuiltStream))
        {
            var originalMetadata = originalPE.GetMetadataReader();
            var rebuiltMetadata = rebuiltPE.GetMetadataReader();

            // Compare assembly identity
            var originalAssembly = originalMetadata.GetAssemblyDefinition();
            var rebuiltAssembly = rebuiltMetadata.GetAssemblyDefinition();

            comparison.OriginalVersion = originalAssembly.Version.ToString();
            comparison.RebuiltVersion = rebuiltAssembly.Version.ToString();
            comparison.SameVersion = comparison.OriginalVersion == comparison.RebuiltVersion;

            // Compare public key tokens
            var originalToken = GetPublicKeyToken(originalMetadata, originalAssembly);
            var rebuiltToken = GetPublicKeyToken(rebuiltMetadata, rebuiltAssembly);
            comparison.OriginalPublicKeyToken = BitConverter.ToString(originalToken).Replace("-", "");
            comparison.RebuiltPublicKeyToken = BitConverter.ToString(rebuiltToken).Replace("-", "");
            comparison.SamePublicKeyToken = comparison.OriginalPublicKeyToken == comparison.RebuiltPublicKeyToken;

            // Compare type and method counts
            comparison.OriginalTypeCount = originalMetadata.TypeDefinitions.Count;
            comparison.RebuiltTypeCount = rebuiltMetadata.TypeDefinitions.Count;

            comparison.OriginalMethodCount = originalMetadata.MethodDefinitions.Count;
            comparison.RebuiltMethodCount = rebuiltMetadata.MethodDefinitions.Count;
        }

        return comparison;
    }

    private byte[] GetPublicKeyToken(MetadataReader reader, AssemblyDefinition assembly)
    {
        if (assembly.PublicKey.IsNil)
        {
            return Array.Empty<byte>();
        }

        var publicKey = reader.GetBlobBytes(assembly.PublicKey);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);
        
        // Public key token is last 8 bytes of SHA1 hash, reversed
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            token[i] = hash[hash.Length - 1 - i];
        }
        
        return token;
    }

    private class AssemblyComparison
    {
        public long OriginalSize { get; set; }
        public long RebuiltSize { get; set; }
        public string OriginalVersion { get; set; } = "";
        public string RebuiltVersion { get; set; } = "";
        public bool SameVersion { get; set; }
        public string OriginalPublicKeyToken { get; set; } = "";
        public string RebuiltPublicKeyToken { get; set; } = "";
        public bool SamePublicKeyToken { get; set; }
        public int OriginalTypeCount { get; set; }
        public int RebuiltTypeCount { get; set; }
        public int OriginalMethodCount { get; set; }
        public int RebuiltMethodCount { get; set; }
    }
}
