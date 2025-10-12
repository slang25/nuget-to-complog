using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
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
        var panel = new Panel($"[cyan]{packageId}[/] {(version != null ? $"[yellow]{version}[/]" : "[dim](latest)[/]")}")
            .Header("[yellow]Processing Package[/]")
            .BorderColor(Color.Cyan1);
        AnsiConsole.Write(panel);
        
        AnsiConsole.MarkupLine($"[dim]Working directory: {_workingDirectory}[/]");
        AnsiConsole.WriteLine();

        try
        {
            // Step 1: Download the NuGet package
            string packagePath = string.Empty;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Downloading package...", async ctx =>
                {
                    packagePath = await DownloadPackageAsync(packageId, version);
                });
            
            AnsiConsole.MarkupLine($"[green]✓[/] Downloaded package to: [dim]{Path.GetFileName(packagePath)}[/]");
            AnsiConsole.WriteLine();

            // Step 2: Extract the package
            var extractPath = Path.Combine(_workingDirectory, "extracted");
            ExtractPackage(packagePath, extractPath);
            AnsiConsole.MarkupLine($"[green]✓[/] Extracted package");
            AnsiConsole.WriteLine();

            // Step 3: Find assemblies in the package
            var allAssemblies = FindAssemblies(extractPath);
            
            var allAssembliesTree = new Tree($"[green]Found {allAssemblies.Count} assemblies across all TFMs[/]");
            foreach (var assembly in allAssemblies)
            {
                var relativePath = Path.GetRelativePath(extractPath, assembly);
                var parts = relativePath.Split(Path.DirectorySeparatorChar);
                var framework = parts.Length > 1 ? parts[1] : "unknown";
                allAssembliesTree.AddNode($"[cyan]{framework}[/] / [yellow]{Path.GetFileName(assembly)}[/]");
            }
            AnsiConsole.Write(allAssembliesTree);
            AnsiConsole.WriteLine();

            // Select the best TFM
            var assemblies = SelectBestTargetFramework(allAssemblies, extractPath);
            if (assemblies.Count > 0)
            {
                var relativePath = Path.GetRelativePath(extractPath, assemblies[0]);
                var parts = relativePath.Split(Path.DirectorySeparatorChar);
                var selectedTfm = parts.Length > 1 ? parts[1] : "unknown";
                AnsiConsole.MarkupLine($"[green]✓[/] Selected best TFM: [cyan]{selectedTfm}[/] with [yellow]{assemblies.Count}[/] assemblies");
                AnsiConsole.WriteLine();
            }

            // Step 4: Try to download symbols package (snupkg)
            string? snupkgPath = null;
            try
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("yellow"))
                    .StartAsync("Attempting to download symbols package (.snupkg)...", async ctx =>
                    {
                        snupkgPath = await DownloadSymbolsPackageAsync(packageId, version);
                    });
                
                if (snupkgPath != null)
                {
                    var symbolsExtractPath = Path.Combine(_workingDirectory, "symbols");
                    ExtractPackage(snupkgPath, symbolsExtractPath);
                    
                    var pdbs = Directory.GetFiles(symbolsExtractPath, "*.pdb", SearchOption.AllDirectories);
                    if (pdbs.Length > 0)
                    {
                        var symbolsTree = new Tree($"[green]✓ Downloaded symbols package with {pdbs.Length} PDB file(s)[/]");
                        foreach (var pdb in pdbs)
                        {
                            symbolsTree.AddNode($"[blue]{Path.GetRelativePath(symbolsExtractPath, pdb)}[/]");
                        }
                        AnsiConsole.Write(symbolsTree);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[green]✓[/] Downloaded symbols package (no PDB files found inside)");
                    }
                    AnsiConsole.WriteLine();
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] Symbols package (.snupkg) not available for this package");
                    AnsiConsole.MarkupLine("   [dim]Note: Not all packages publish symbol packages to NuGet.org[/]");
                    AnsiConsole.WriteLine();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]⚠[/] Could not download symbols package: [dim]{ex.Message}[/]");
                AnsiConsole.WriteLine();
            }

            // Step 5: Process each assembly to extract compiler arguments
            foreach (var assemblyPath in assemblies)
            {
                var assemblyPanel = new Panel($"[cyan]{Path.GetFileName(assemblyPath)}[/]")
                    .Header("[yellow]Analyzing Assembly[/]")
                    .BorderColor(Color.Yellow)
                    .Expand();
                AnsiConsole.Write(assemblyPanel);
                
                var pdbExtractor = new PdbCompilerArgumentsExtractor();
                await pdbExtractor.ExtractCompilerArgumentsAsync(assemblyPath, _workingDirectory);
                
                AnsiConsole.WriteLine();
            }

            // Step 6: Output next steps and considerations
            var nextStepsPanel = new Panel(
                new Markup(@"[yellow]TODO: Implement the following to create a complete complog:[/]

[cyan]1. DEPENDENCY RESOLUTION:[/]
   • Parse the .nuspec file from the extracted package
   • Recursively download all package dependencies based on target framework
   • Handle dependency version ranges and resolve to specific versions
   • Consider using NuGet.DependencyResolver or NuGet.Packaging APIs

[cyan]2. FRAMEWORK ASSEMBLIES:[/]
   • Identify the target framework from compiler arguments (e.g., net8.0, netstandard2.0)
   • Download or locate the reference assemblies for that framework
   • Options:
     a) Use NuGet.Frameworks to identify framework
     b) Download reference assemblies from nuget.org (e.g., Microsoft.NETCore.App.Ref)
     c) Use local SDK reference assemblies if available
   
[cyan]3. SOURCE CODE EXTRACTION:[/]
   • Source files may be embedded in the PDB (Embedded Source)
   • Or referenced via Source Link URLs (need to download from git repos)
   • Parse Source Link JSON from PDB to map files to URLs
   • Download source files and preserve directory structure

[cyan]4. COMPLOG PACKAGING:[/]
   • Create a standardized directory structure
   • Package all references, sources, and compiler arguments
   • Ensure deterministic/reproducible build capability
   • Consider compression format (zip, tar.gz, or custom)

[cyan]5. VALIDATION:[/]
   • Verify we have all required references
   • Check that source files match the compiled assembly
   • Validate compiler arguments are complete
   • Test that we can recreate the Roslyn workspace

[yellow]LIMITATIONS TO HANDLE:[/]
• Not all packages are built with deterministic builds enabled
• Not all packages include symbols (PDB files)
• Some packages may have embedded PDBs in assemblies
• Source Link may not be configured or repos may be private
• Multi-targeting packages (need to pick or process all TFMs)"))
                .Header("[green]Next Steps for CompLog Creation[/]")
                .BorderColor(Color.Green)
                .Expand();
            
            AnsiConsole.Write(nextStepsPanel);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
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
        
        // Try multiple symbol package sources
        var snupkgUrls = new[]
        {
            // Primary NuGet feed (v3-flatcontainer)
            $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToNormalizedString()}/{packageId.ToLowerInvariant()}.{version.ToNormalizedString()}.snupkg",
            // Alternative: globalpackages cache format (some packages use this)
            $"https://globalcdn.nuget.org/packages/{packageId.ToLowerInvariant()}.{version.ToNormalizedString()}.snupkg",
        };

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        
        foreach (var snupkgUrl in snupkgUrls)
        {
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
                // Try next URL
            }
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

    private List<string> SelectBestTargetFramework(List<string> assemblies, string extractPath)
    {
        if (assemblies.Count == 0)
            return assemblies;

        // Group assemblies by their target framework
        var groupedByTfm = assemblies
            .GroupBy(assembly =>
            {
                var relativePath = Path.GetRelativePath(extractPath, assembly);
                var parts = relativePath.Split(Path.DirectorySeparatorChar);
                // TFM is typically the folder after lib/ or ref/
                return parts.Length > 1 ? parts[1] : "unknown";
            })
            .ToList();

        // If only one TFM, return all assemblies
        if (groupedByTfm.Count == 1)
            return assemblies;

        // Select the best TFM based on priority and version
        var bestTfm = groupedByTfm
            .OrderByDescending(g => GetTargetFrameworkPriority(g.Key))
            .ThenByDescending(g => GetTargetFrameworkVersion(g.Key))
            .First();

        return bestTfm.ToList();
    }

    private (int priority, int major, int minor) GetTargetFrameworkPriority(string tfm)
    {
        tfm = tfm.ToLowerInvariant();

        // .NET (net5.0+) - highest priority
        if (tfm.StartsWith("net") && !tfm.StartsWith("netstandard") && !tfm.StartsWith("netcoreapp") && !tfm.StartsWith("netframework"))
        {
            // Check if it's .NET 5+ (single digit after 'net')
            var versionPart = tfm.Substring(3);
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]))
            {
                var firstChar = versionPart[0];
                // net5.0, net6.0, net7.0, net8.0, net9.0, etc.
                if (firstChar >= '5')
                {
                    var version = ParseVersion(versionPart);
                    return (3, version.major, version.minor); // Priority 3 for modern .NET
                }
            }
        }

        // .NET Standard - second priority
        if (tfm.StartsWith("netstandard"))
        {
            var versionPart = tfm.Substring("netstandard".Length);
            var version = ParseVersion(versionPart);
            return (2, version.major, version.minor);
        }

        // .NET Core App
        if (tfm.StartsWith("netcoreapp"))
        {
            var versionPart = tfm.Substring("netcoreapp".Length);
            var version = ParseVersion(versionPart);
            return (2, version.major, version.minor);
        }

        // .NET Framework - third priority
        if (tfm.StartsWith("net") && (tfm.Contains("framework") || char.IsDigit(tfm[3])))
        {
            var versionPart = tfm.Replace("framework", "");
            if (versionPart.StartsWith("net"))
                versionPart = versionPart.Substring(3);
            var version = ParseVersion(versionPart);
            return (1, version.major, version.minor);
        }

        // Unknown/other - lowest priority
        return (0, 0, 0);
    }

    private (int major, int minor) GetTargetFrameworkVersion(string tfm)
    {
        var (_, major, minor) = GetTargetFrameworkPriority(tfm);
        return (major, minor);
    }

    private (int major, int minor) ParseVersion(string versionPart)
    {
        if (string.IsNullOrEmpty(versionPart))
            return (0, 0);

        // Handle versions like "8.0", "9.0", "48", "472", "2.0", "2.1"
        versionPart = versionPart.TrimStart('v');
        
        // Split by dot if present
        var parts = versionPart.Split('.');
        if (parts.Length >= 2)
        {
            if (int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
                return (major, minor);
        }
        else if (parts.Length == 1)
        {
            // Handle compact versions like "48" (net48), "472" (net472)
            if (int.TryParse(parts[0], out var version))
            {
                if (version >= 10)
                {
                    // Split into major.minor (e.g., 48 -> 4.8, 472 -> 4.7.2)
                    var major = version / 10;
                    var minor = version % 10;
                    return (major, minor);
                }
                else
                {
                    return (version, 0);
                }
            }
        }

        return (0, 0);
    }
}
