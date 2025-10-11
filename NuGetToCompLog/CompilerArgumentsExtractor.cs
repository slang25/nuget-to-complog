using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.IO.Compression;

namespace NuGetToCompLog;

/// <summary>
/// Main orchestrator for downloading NuGet packages and extracting compiler arguments.
/// 
/// This class handles:
/// 1. Downloading packages from nuget.org (both .nupkg and .snupkg files)
/// 2. Extracting package contents to a temporary directory
/// 3. Locating assemblies within the package (lib/ and ref/ folders)
/// 4. Coordinating PDB discovery and compiler argument extraction
/// 5. Displaying next steps for creating a complete CompLog file
/// 
/// Future enhancements should include:
/// - Dependency resolution and recursive package downloads
/// - Framework assembly resolution and download
/// - Source code extraction from PDBs or Source Link
/// - CompLog file packaging with all required artifacts
/// </summary>
public class CompilerArgumentsExtractor
{
    private readonly string _workingDirectory;
    private readonly ILogger _logger;

    public CompilerArgumentsExtractor()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), "nuget-to-complog", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workingDirectory);
        _logger = NullLogger.Instance;
    }

    public async Task ProcessPackageAsync(string packageId, string? version)
    {
        Console.WriteLine($"Processing package: {packageId} {version ?? "(latest)"}");
        Console.WriteLine($"Working directory: {_workingDirectory}");
        Console.WriteLine();

        try
        {
            // Step 1: Download the NuGet package
            var packagePath = await DownloadPackageAsync(packageId, version);
            Console.WriteLine($"✓ Downloaded package to: {packagePath}");
            Console.WriteLine();

            // Step 2: Extract the package
            var extractPath = Path.Combine(_workingDirectory, "extracted");
            ExtractPackage(packagePath, extractPath);
            Console.WriteLine($"✓ Extracted package to: {extractPath}");
            Console.WriteLine();

            // Step 3: Find assemblies in the package
            var assemblies = FindAssemblies(extractPath);
            Console.WriteLine($"✓ Found {assemblies.Count} assemblies:");
            foreach (var assembly in assemblies)
            {
                Console.WriteLine($"  - {Path.GetRelativePath(extractPath, assembly)}");
            }
            Console.WriteLine();

            // Step 4: Try to download symbols package (snupkg)
            string? snupkgPath = null;
            try
            {
                Console.WriteLine("Attempting to download symbols package (.snupkg)...");
                snupkgPath = await DownloadSymbolsPackageAsync(packageId, version);
                if (snupkgPath != null)
                {
                    var symbolsExtractPath = Path.Combine(_workingDirectory, "symbols");
                    ExtractPackage(snupkgPath, symbolsExtractPath);
                    Console.WriteLine($"✓ Downloaded and extracted symbols package to: {symbolsExtractPath}");
                    
                    var pdbs = Directory.GetFiles(symbolsExtractPath, "*.pdb", SearchOption.AllDirectories);
                    Console.WriteLine($"  Found {pdbs.Length} PDB files:");
                    foreach (var pdb in pdbs)
                    {
                        Console.WriteLine($"    - {Path.GetRelativePath(symbolsExtractPath, pdb)}");
                    }
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine("⚠ Symbols package (.snupkg) not found");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Could not download symbols package: {ex.Message}");
                Console.WriteLine();
            }

            // Step 5: Process each assembly to extract compiler arguments
            foreach (var assemblyPath in assemblies)
            {
                Console.WriteLine($"Processing assembly: {Path.GetFileName(assemblyPath)}");
                Console.WriteLine(new string('=', 80));
                
                var pdbExtractor = new PdbCompilerArgumentsExtractor();
                await pdbExtractor.ExtractCompilerArgumentsAsync(assemblyPath, _workingDirectory);
                
                Console.WriteLine();
            }

            // Step 6: Output next steps and considerations
            Console.WriteLine("NEXT STEPS FOR COMPLOG CREATION:");
            Console.WriteLine(new string('=', 80));
            Console.WriteLine(@"
TODO: Implement the following to create a complete complog:

1. DEPENDENCY RESOLUTION:
   - Parse the .nuspec file from the extracted package
   - Recursively download all package dependencies based on target framework
   - Handle dependency version ranges and resolve to specific versions
   - Consider using NuGet.DependencyResolver or NuGet.Packaging APIs

2. FRAMEWORK ASSEMBLIES:
   - Identify the target framework from compiler arguments (e.g., net8.0, netstandard2.0)
   - Download or locate the reference assemblies for that framework
   - Options:
     a) Use NuGet.Frameworks to identify framework
     b) Download reference assemblies from nuget.org (e.g., Microsoft.NETCore.App.Ref)
     c) Use local SDK reference assemblies if available
   
3. SOURCE CODE EXTRACTION:
   - Source files may be embedded in the PDB (Embedded Source)
   - Or referenced via Source Link URLs (need to download from git repos)
   - Parse Source Link JSON from PDB to map files to URLs
   - Download source files and preserve directory structure

4. COMPLOG PACKAGING:
   - Create a standardized directory structure
   - Package all references, sources, and compiler arguments
   - Ensure deterministic/reproducible build capability
   - Consider compression format (zip, tar.gz, or custom)

5. VALIDATION:
   - Verify we have all required references
   - Check that source files match the compiled assembly
   - Validate compiler arguments are complete
   - Test that we can recreate the Roslyn workspace

LIMITATIONS TO HANDLE:
- Not all packages are built with deterministic builds enabled
- Not all packages include symbols (PDB files)
- Some packages may have embedded PDBs in assemblies
- Source Link may not be configured or repos may be private
- Multi-targeting packages (need to pick or process all TFMs)
");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private async Task<string> DownloadPackageAsync(string packageId, string? versionString)
    {
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

        NuGetVersion version;
        if (versionString == null)
        {
            // Get all versions and pick the latest stable
            var versions = await resource.GetAllVersionsAsync(
                packageId,
                cache,
                _logger,
                CancellationToken.None);

            version = versions
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .FirstOrDefault() ?? versions.OrderByDescending(v => v).First();

            Console.WriteLine($"Latest version: {version}");
        }
        else
        {
            version = NuGetVersion.Parse(versionString);
        }

        var packagePath = Path.Combine(_workingDirectory, $"{packageId}.{version}.nupkg");

        using var packageStream = File.Create(packagePath);
        var success = await resource.CopyNupkgToStreamAsync(
            packageId,
            version,
            packageStream,
            cache,
            _logger,
            CancellationToken.None);

        if (!success)
        {
            throw new Exception($"Failed to download package {packageId} {version}");
        }

        return packagePath;
    }

    private async Task<string?> DownloadSymbolsPackageAsync(string packageId, string? versionString)
    {
        // NuGet symbols packages are hosted at a different feed
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

        NuGetVersion version;
        if (versionString == null)
        {
            var versions = await resource.GetAllVersionsAsync(
                packageId,
                cache,
                _logger,
                CancellationToken.None);

            version = versions
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .FirstOrDefault() ?? versions.OrderByDescending(v => v).First();
        }
        else
        {
            version = NuGetVersion.Parse(versionString);
        }

        // Try to download the .snupkg file
        var snupkgPath = Path.Combine(_workingDirectory, $"{packageId}.{version}.snupkg");
        
        // Note: The NuGet client doesn't have direct support for .snupkg downloads
        // We need to construct the URL manually
        var snupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToNormalizedString()}/{packageId.ToLowerInvariant()}.{version.ToNormalizedString()}.snupkg";

        using var httpClient = new HttpClient();
        try
        {
            var response = await httpClient.GetAsync(snupkgUrl);
            if (response.IsSuccessStatusCode)
            {
                using var fileStream = File.Create(snupkgPath);
                await response.Content.CopyToAsync(fileStream);
                return snupkgPath;
            }
        }
        catch
        {
            // Symbols package doesn't exist
        }

        return null;
    }

    private void ExtractPackage(string packagePath, string extractPath)
    {
        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
    }

    private List<string> FindAssemblies(string extractPath)
    {
        // Look for assemblies in lib folder (typical for NuGet packages)
        var assemblies = new List<string>();
        
        var libPath = Path.Combine(extractPath, "lib");
        if (Directory.Exists(libPath))
        {
            assemblies.AddRange(Directory.GetFiles(libPath, "*.dll", SearchOption.AllDirectories));
        }

        // Also check ref folder (reference assemblies)
        var refPath = Path.Combine(extractPath, "ref");
        if (Directory.Exists(refPath))
        {
            assemblies.AddRange(Directory.GetFiles(refPath, "*.dll", SearchOption.AllDirectories));
        }

        return assemblies;
    }
}
