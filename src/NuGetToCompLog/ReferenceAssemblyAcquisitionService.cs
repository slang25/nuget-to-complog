using System.Reflection.Metadata;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace NuGetToCompLog;

/// <summary>
/// Service for acquiring reference assemblies from both framework reference packages
/// and NuGet packages. Uses a multi-tiered approach:
/// 1. Download framework reference assemblies from NuGet (most portable)
/// 2. Fallback to local SDK if available
/// 3. Download NuGet package dependencies
/// </summary>
public class ReferenceAssemblyAcquisitionService
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly HashSet<string> _acquiredPackages = [];
    private readonly Dictionary<string, string> _frameworkToPackageMap = new();
    private readonly string _workingDirectory;
    
    public ReferenceAssemblyAcquisitionService(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        InitializeFrameworkPackageMap();
    }

    /// <summary>
    /// Maps target framework monikers to their corresponding reference assembly NuGet packages.
    /// </summary>
    private void InitializeFrameworkPackageMap()
    {
        // .NET (5.0+)
        _frameworkToPackageMap["net5.0"] = "Microsoft.NETCore.App.Ref/5.0.0";
        _frameworkToPackageMap["net6.0"] = "Microsoft.NETCore.App.Ref/6.0.0";
        _frameworkToPackageMap["net7.0"] = "Microsoft.NETCore.App.Ref/7.0.0";
        _frameworkToPackageMap["net8.0"] = "Microsoft.NETCore.App.Ref/8.0.0";
        _frameworkToPackageMap["net9.0"] = "Microsoft.NETCore.App.Ref/9.0.0";
        
        // .NET Standard 2.x - use NETStandard.Library.Ref
        _frameworkToPackageMap["netstandard2.0"] = "NETStandard.Library.Ref/2.1.0";
        _frameworkToPackageMap["netstandard2.1"] = "NETStandard.Library.Ref/2.1.0";
        
        // .NET Standard 1.x - use NETStandard.Library (different package structure)
        _frameworkToPackageMap["netstandard1.0"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.1"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.2"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.3"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.4"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.5"] = "NETStandard.Library/1.6.1";
        _frameworkToPackageMap["netstandard1.6"] = "NETStandard.Library/1.6.1";
        
        // .NET Framework
        _frameworkToPackageMap["net45"] = "Microsoft.NETFramework.ReferenceAssemblies.net45/1.0.3";
        _frameworkToPackageMap["net451"] = "Microsoft.NETFramework.ReferenceAssemblies.net451/1.0.3";
        _frameworkToPackageMap["net452"] = "Microsoft.NETFramework.ReferenceAssemblies.net452/1.0.3";
        _frameworkToPackageMap["net46"] = "Microsoft.NETFramework.ReferenceAssemblies.net46/1.0.3";
        _frameworkToPackageMap["net461"] = "Microsoft.NETFramework.ReferenceAssemblies.net461/1.0.3";
        _frameworkToPackageMap["net462"] = "Microsoft.NETFramework.ReferenceAssemblies.net462/1.0.3";
        _frameworkToPackageMap["net47"] = "Microsoft.NETFramework.ReferenceAssemblies.net47/1.0.3";
        _frameworkToPackageMap["net471"] = "Microsoft.NETFramework.ReferenceAssemblies.net471/1.0.3";
        _frameworkToPackageMap["net472"] = "Microsoft.NETFramework.ReferenceAssemblies.net472/1.0.3";
        _frameworkToPackageMap["net48"] = "Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3";
    }

    /// <summary>
    /// Acquires all reference assemblies needed for the compilation.
    ///
    /// When the PDB recorded reference MVIDs, they are the source of truth: a same-named
    /// assembly from a different targeting pack or package version produces an identical
    /// compilation *result* but a different metadata-references blob in the rebuilt PDB,
    /// breaking byte-for-byte reproduction. So candidates are verified by MVID and the exact
    /// targeting pack version is located when the local SDK doesn't have it.
    /// </summary>
    public async Task<Dictionary<string, string>> AcquireAllReferencesAsync(
        List<MetadataReference> references,
        string targetFramework)
    {
        var acquiredReferences = new Dictionary<string, string>(); // fileName -> localPath

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[yellow]Acquiring Reference Assemblies:[/]");
        AnsiConsole.MarkupLine($"  [dim]→ Target framework: {targetFramework ?? "unknown"}[/]");

        var frameworkRefs = new List<MetadataReference>();
        var nugetRefs = new List<MetadataReference>();

        foreach (var reference in references)
        {
            if (IsFrameworkAssembly(reference.FileName))
            {
                frameworkRefs.Add(reference);
            }
            else if (IsNuGetPackageReference(reference.FileName))
            {
                nugetRefs.Add(reference);
            }
        }

        AnsiConsole.MarkupLine($"  [cyan]Framework assemblies:[/] {frameworkRefs.Count}");
        AnsiConsole.MarkupLine($"  [cyan]NuGet package references:[/] {nugetRefs.Count}");
        AnsiConsole.WriteLine();

        var frameworkPaths = new Dictionary<string, string>();
        if (frameworkRefs.Count > 0 && !string.IsNullOrEmpty(targetFramework))
        {
            frameworkPaths = await AcquireFrameworkReferencesAsync(targetFramework, frameworkRefs);
        }

        var nugetPaths = new Dictionary<string, string>();
        if (nugetRefs.Count > 0 && !string.IsNullOrEmpty(targetFramework))
        {
            nugetPaths = await AcquireNuGetReferencesAsync(nugetRefs, targetFramework);
        }

        // Resolve each expected reference to the candidate matching its recorded MVID;
        // when neither matches (or no MVID was recorded), framework wins over package to
        // keep old NuGet versions from shadowing framework assemblies.
        var mvidMatched = 0;
        var mvidMismatched = new List<string>();
        var withMvid = 0;
        foreach (var reference in references)
        {
            var fileName = Path.GetFileName(reference.FileName);
            frameworkPaths.TryGetValue(fileName, out var frameworkPath);
            nugetPaths.TryGetValue(fileName, out var nugetPath);

            if (frameworkPath == null && nugetPath == null)
            {
                continue;
            }

            string chosen;
            if (reference.Mvid != Guid.Empty)
            {
                withMvid++;
                if (frameworkPath != null && TryReadMvid(frameworkPath) == reference.Mvid)
                {
                    chosen = frameworkPath;
                    mvidMatched++;
                }
                else if (nugetPath != null && TryReadMvid(nugetPath) == reference.Mvid)
                {
                    chosen = nugetPath;
                    mvidMatched++;
                }
                else
                {
                    chosen = frameworkPath ?? nugetPath!;
                    mvidMismatched.Add(fileName);
                }
            }
            else
            {
                chosen = frameworkPath ?? nugetPath!;
            }

            acquiredReferences[fileName] = chosen;
        }

        // For package references that still mismatch, hunt through the package's other
        // versions on nuget.org for the exact assembly.
        if (mvidMismatched.Count > 0)
        {
            var recovered = await RecoverExactPackageVersionsAsync(
                references.Where(r => mvidMismatched.Contains(Path.GetFileName(r.FileName)) && r.Mvid != Guid.Empty).ToList(),
                targetFramework!,
                acquiredReferences);
            mvidMatched += recovered;
            if (recovered > 0)
            {
                mvidMismatched = mvidMismatched
                    .Where(f => acquiredReferences.TryGetValue(f, out var p) &&
                                TryReadMvid(p) != references.First(r => Path.GetFileName(r.FileName) == f).Mvid)
                    .ToList();
            }
        }

        if (withMvid > 0)
        {
            if (mvidMismatched.Count == 0)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] All {mvidMatched} references verified against PDB-recorded MVIDs");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] {mvidMismatched.Count} reference(s) do not match the original build's MVIDs:");
                foreach (var name in mvidMismatched.Take(5))
                {
                    AnsiConsole.MarkupLine($"    [dim]• {name}[/]");
                }
                if (mvidMismatched.Count > 5)
                {
                    AnsiConsole.MarkupLine($"    [dim]... and {mvidMismatched.Count - 5} more[/]");
                }
            }
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Acquired {acquiredReferences.Count} reference assemblies");
        return acquiredReferences;
    }

    /// <summary>
    /// Reads the MVID from an assembly on disk, or null if unreadable.
    /// </summary>
    internal static Guid? TryReadMvid(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
            var reader = peReader.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Acquires framework reference assemblies by downloading the appropriate NuGet package.
    /// </summary>
    private async Task<Dictionary<string, string>> AcquireFrameworkReferencesAsync(
        string targetFramework,
        List<MetadataReference> frameworkRefs)
    {
        var result = new Dictionary<string, string>();
        


        if (targetFramework.StartsWith("netstandard1"))
        {
            AnsiConsole.MarkupLine($"  [cyan]→[/] Downloading individual System.* packages for {targetFramework}...");
            return await DownloadIndividualSystemPackagesAsync(targetFramework, frameworkRefs);
        }


        var sdkResult = await TryAcquireFromLocalSdkAsync(targetFramework, frameworkRefs);
        if (sdkResult.Count > 0 && FrameworkRefsMatchByMvid(sdkResult, frameworkRefs))
        {
            return sdkResult;
        }

        // The local SDK doesn't have the exact targeting pack the original build used (or has
        // none at all). When MVIDs are known, locate the exact pack version on nuget.org.
        if (frameworkRefs.Any(r => r.Mvid != Guid.Empty))
        {
            var exactResult = await TryAcquireExactRefPackFromNuGetAsync(targetFramework, frameworkRefs);
            if (exactResult.Count > 0)
            {
                return exactResult;
            }
        }

        if (sdkResult.Count > 0)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] Using local SDK reference assemblies (exact targeting pack not found)");
            return sdkResult;
        }


        if (!_frameworkToPackageMap.TryGetValue(targetFramework, out var packageInfo))
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] No reference package mapping for {targetFramework}");
            return result;
        }

        var parts = packageInfo.Split('/');
        var packageId = parts[0];
        var version = parts[1];


        if (_acquiredPackages.Contains(packageInfo))
        {
            AnsiConsole.MarkupLine($"  [dim]→ Using cached {packageId}[/]");
            return await ExtractReferencesFromCachedPackageAsync(packageId, version, targetFramework, frameworkRefs);
        }

        AnsiConsole.MarkupLine($"  [cyan]→[/] Downloading framework references: {packageId} {version}");

        try
        {

            var packagePath = await DownloadPackageAsync(packageId, version);
            _acquiredPackages.Add(packageInfo);


            var extractPath = Path.Combine(_workingDirectory, "framework-refs", $"{packageId}.{version}");
            Directory.CreateDirectory(extractPath);
            System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);


            List<string> possiblePaths = new();
            

            possiblePaths.Add(Path.Combine(extractPath, "ref", targetFramework));

            if (targetFramework.Contains("."))
            {
                var tfmWithoutPatch = string.Join(".", targetFramework.Split('.').Take(2));
                possiblePaths.Add(Path.Combine(extractPath, "ref", tfmWithoutPatch));
            }
            

            foreach (var refDir in possiblePaths)
            {
                if (Directory.Exists(refDir))
                {
                    var dlls = Directory.GetFiles(refDir, "*.dll", SearchOption.TopDirectoryOnly);
                    if (dlls.Length > 0)
                    {
                        foreach (var dll in dlls)
                        {
                            var fileName = Path.GetFileName(dll);
                            result[fileName] = dll;
                        }
                        
                        AnsiConsole.MarkupLine($"    [green]✓[/] Extracted {result.Count} framework assemblies from {Path.GetRelativePath(extractPath, refDir)}");
                        break;
                    }
                }
            }
            
            if (result.Count == 0)
            {
                AnsiConsole.MarkupLine($"    [yellow]⚠[/] Package has no assemblies for {targetFramework}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"    [yellow]⚠[/] Failed to download framework references: {ex.Message}");
        }

        return result;
    }
    
    /// <summary>
    /// For .NET Standard 1.x, download individual System.* packages since the meta-package has no DLLs.
    /// </summary>
    private async Task<Dictionary<string, string>> DownloadIndividualSystemPackagesAsync(
        string targetFramework,
        List<MetadataReference> frameworkRefs)
    {
        var result = new Dictionary<string, string>();
        var packageVersion = "4.3.0"; // Standard version for all System.* packages
        

        var packageNames = frameworkRefs
            .Select(r => Path.GetFileNameWithoutExtension(r.FileName))
            .Where(name => name.StartsWith("System.") || name == "Microsoft.CSharp")
            .Distinct()
            .ToList();
        
        AnsiConsole.MarkupLine($"  [dim]Downloading {packageNames.Count} individual packages...[/]");
        
        var downloadedCount = 0;
        foreach (var packageName in packageNames)
        {
            try
            {
                var packageKey = $"{packageName}/{packageVersion}";
                if (_acquiredPackages.Contains(packageKey))
                {
                    continue; // Already downloaded
                }
                
                var packagePath = await DownloadPackageAsync(packageName, packageVersion);
                _acquiredPackages.Add(packageKey);
                

                var extractPath = Path.Combine(_workingDirectory, "framework-refs", $"{packageName}.{packageVersion}");
                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
                

                var refPath = Path.Combine(extractPath, "ref", targetFramework);
                if (Directory.Exists(refPath))
                {
                    var dlls = Directory.GetFiles(refPath, "*.dll", SearchOption.TopDirectoryOnly);
                    foreach (var dll in dlls)
                    {
                        var fileName = Path.GetFileName(dll);
                        if (!result.ContainsKey(fileName))
                        {
                            result[fileName] = dll;
                            downloadedCount++;
                        }
                    }
                }
            }
            catch (Exception)
            {

                continue;
            }
        }
        
        if (downloadedCount > 0)
        {
            AnsiConsole.MarkupLine($"    [green]✓[/] Downloaded and extracted {downloadedCount} framework assemblies");
        }
        
        return result;
    }

    /// <summary>
    /// Fallback: Try to acquire framework assemblies from the local .NET SDK. When the PDB
    /// recorded reference MVIDs, the installed targeting pack version that actually matches
    /// them is preferred over simply the newest.
    /// </summary>
    private async Task<Dictionary<string, string>> TryAcquireFromLocalSdkAsync(
        string targetFramework,
        List<MetadataReference> frameworkRefs)
    {
        var result = new Dictionary<string, string>();

        var dotnetRoot = GetDotNetRoot();
        if (dotnetRoot == null)
        {
            return result;
        }

        var packsDir = Path.Combine(dotnetRoot, "packs");
        if (!Directory.Exists(packsDir))
        {
            return result;
        }

        var packNames = targetFramework.StartsWith("netstandard")
            ? new[] { "Microsoft.NETCore.App.Ref", "NETStandard.Library.Ref" }
            : ["Microsoft.NETCore.App.Ref"];

        var candidateDirs = packNames
            .Select(name => Path.Combine(packsDir, name))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .OrderByDescending(d => d)
            .Select(versionDir => Path.Combine(versionDir, "ref", targetFramework))
            .Where(refPath => Directory.Exists(refPath) &&
                              Directory.GetFiles(refPath, "*.dll", SearchOption.TopDirectoryOnly).Length > 0)
            .ToList();

        if (candidateDirs.Count == 0)
        {
            return result;
        }

        var chosen = candidateDirs.FirstOrDefault(dir => DirectoryMatchesMvids(dir, frameworkRefs))
                     ?? candidateDirs[0];

        foreach (var dll in Directory.GetFiles(chosen, "*.dll", SearchOption.TopDirectoryOnly))
        {
            result[Path.GetFileName(dll)] = dll;
        }

        AnsiConsole.MarkupLine($"    [green]✓[/] Found {result.Count} framework assemblies in local SDK");
        return await Task.FromResult(result);
    }

    /// <summary>
    /// Probes a few assemblies in a directory against the MVIDs recorded in the PDB. Targeting
    /// packs are internally consistent, so a handful of matches identifies the pack version.
    /// </summary>
    private static bool DirectoryMatchesMvids(string directory, List<MetadataReference> references, int probeCount = 5)
    {
        var probes = references
            .Where(r => r.Mvid != Guid.Empty)
            .Select(r => (r.Mvid, Path: Path.Combine(directory, Path.GetFileName(r.FileName))))
            .Where(p => File.Exists(p.Path))
            .Take(probeCount)
            .ToList();

        return probes.Count > 0 && probes.All(p => TryReadMvid(p.Path) == p.Mvid);
    }

    private static bool FrameworkRefsMatchByMvid(Dictionary<string, string> acquired, List<MetadataReference> references, int probeCount = 5)
    {
        var probes = references
            .Where(r => r.Mvid != Guid.Empty && acquired.ContainsKey(Path.GetFileName(r.FileName)))
            .Take(probeCount)
            .ToList();

        // No MVIDs recorded (or nothing acquired to probe): nothing to verify against, accept.
        if (probes.Count == 0)
        {
            return true;
        }

        return probes.All(p => TryReadMvid(acquired[Path.GetFileName(p.FileName)]) == p.Mvid);
    }

    /// <summary>
    /// Locates the exact targeting pack version on nuget.org by probing candidate versions'
    /// assemblies against the MVIDs recorded in the PDB. Versions are probed newest-first by
    /// reading a single assembly out of each candidate .nupkg before committing to a full extract.
    /// </summary>
    private async Task<Dictionary<string, string>> TryAcquireExactRefPackFromNuGetAsync(
        string targetFramework,
        List<MetadataReference> frameworkRefs)
    {
        var result = new Dictionary<string, string>();

        if (!_frameworkToPackageMap.TryGetValue(targetFramework, out var packageInfo))
        {
            return result;
        }
        var packageId = packageInfo.Split('/')[0];

        var probe = frameworkRefs.FirstOrDefault(r => r.Mvid != Guid.Empty);
        if (probe == null)
        {
            return result;
        }
        var probeFileName = Path.GetFileName(probe.FileName);

        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
            var allVersions = await resource.GetAllVersionsAsync(packageId, cache, _logger, CancellationToken.None);

            // Restrict to the target framework's major.minor (e.g. net9.0 -> 9.0.x).
            var tfmVersion = NuGetFramework.Parse(targetFramework).Version;
            var candidates = allVersions
                .Where(v => !v.IsPrerelease && v.Major == tfmVersion.Major && v.Minor == tfmVersion.Minor)
                .OrderByDescending(v => v)
                .Take(30)
                .ToList();

            AnsiConsole.MarkupLine($"  [cyan]→[/] Probing {packageId} versions on nuget.org for exact reference MVIDs...");

            foreach (var version in candidates)
            {
                var packagePath = await DownloadPackageAsync(packageId, version.ToNormalizedString());

                using (var archive = System.IO.Compression.ZipFile.OpenRead(packagePath))
                {
                    var entry = archive.GetEntry($"ref/{targetFramework}/{probeFileName}");
                    if (entry == null)
                    {
                        continue;
                    }

                    using var entryStream = entry.Open();
                    using var buffer = new MemoryStream();
                    await entryStream.CopyToAsync(buffer);
                    using var peReader = new System.Reflection.PortableExecutable.PEReader(
                        System.Collections.Immutable.ImmutableArray.Create(buffer.ToArray()));
                    var reader = peReader.GetMetadataReader();
                    if (reader.GetGuid(reader.GetModuleDefinition().Mvid) != probe.Mvid)
                    {
                        continue;
                    }
                }

                var extractPath = Path.Combine(_workingDirectory, "framework-refs", $"{packageId}.{version}");
                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);

                var refDir = Path.Combine(extractPath, "ref", targetFramework);
                foreach (var dll in Directory.GetFiles(refDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    result[Path.GetFileName(dll)] = dll;
                }

                AnsiConsole.MarkupLine($"    [green]✓[/] Exact targeting pack: {packageId} {version} ({result.Count} assemblies)");
                return result;
            }

            AnsiConsole.MarkupLine($"    [yellow]⚠[/] No {packageId} version matched the recorded MVIDs");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"    [yellow]⚠[/] Failed probing targeting pack versions: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// For package references whose acquired assembly doesn't match the PDB-recorded MVID
    /// (the PDB stores only file names, so acquisition guesses the version), probes other
    /// versions of the package for the exact assembly.
    /// </summary>
    private async Task<int> RecoverExactPackageVersionsAsync(
        List<MetadataReference> mismatchedRefs,
        string targetFramework,
        Dictionary<string, string> acquiredReferences)
    {
        var recovered = 0;

        foreach (var reference in mismatchedRefs)
        {
            var fileName = Path.GetFileName(reference.FileName);
            var packageInfo = ExtractPackageInfoFromPath(reference.FileName);
            if (packageInfo == null)
            {
                continue;
            }

            var exactPath = await TryFindExactPackageAssemblyAsync(
                packageInfo.Value.PackageId, fileName, reference.Mvid, targetFramework);
            if (exactPath != null)
            {
                acquiredReferences[fileName] = exactPath;
                recovered++;
            }
        }

        return recovered;
    }

    private async Task<string?> TryFindExactPackageAssemblyAsync(
        string packageId,
        string fileName,
        Guid expectedMvid,
        string targetFramework,
        int maxProbes = 15)
    {
        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
            var versions = (await resource.GetAllVersionsAsync(packageId, cache, _logger, CancellationToken.None))
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .Take(maxProbes)
                .ToList();

            foreach (var version in versions)
            {
                var packagePath = await DownloadPackageAsync(packageId, version.ToNormalizedString());
                var extractPath = Path.Combine(_workingDirectory, "nuget-refs", $"{packageId}.{version}");
                if (!Directory.Exists(extractPath))
                {
                    Directory.CreateDirectory(extractPath);
                    System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
                }

                foreach (var folder in new[] { "ref", "lib" })
                {
                    var folderPath = Path.Combine(extractPath, folder);
                    if (!Directory.Exists(folderPath))
                    {
                        continue;
                    }

                    var bestMatch = SelectBestTfmFolder(Directory.GetDirectories(folderPath), targetFramework);
                    if (bestMatch == null)
                    {
                        continue;
                    }

                    var candidate = Path.Combine(bestMatch, fileName);
                    if (File.Exists(candidate) && TryReadMvid(candidate) == expectedMvid)
                    {
                        AnsiConsole.MarkupLine($"    [green]✓[/] Exact package assembly: {packageId} {version} for {fileName}");
                        return candidate;
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// Acquires NuGet package reference assemblies and their transitive dependencies.
    /// </summary>
    private async Task<Dictionary<string, string>> AcquireNuGetReferencesAsync(
        List<MetadataReference> nugetRefs,
        string targetFramework)
    {
        var result = new Dictionary<string, string>();
        var packageGroups = new Dictionary<string, (List<MetadataReference> Refs, string? Version)>();


        foreach (var reference in nugetRefs)
        {
            var packageInfo = ExtractPackageInfoFromPath(reference.FileName);
            if (packageInfo.HasValue)
            {
                var packageId = packageInfo.Value.PackageId;
                var version = packageInfo.Value.Version; // May be null
                
                if (!packageGroups.ContainsKey(packageId))
                {
                    packageGroups[packageId] = (new List<MetadataReference>(), version);
                }
                packageGroups[packageId].Refs.Add(reference);
            }
        }

        AnsiConsole.MarkupLine($"  [cyan]→[/] Downloading {packageGroups.Count} NuGet packages and their dependencies...");


        var allPackages = new Dictionary<string, string>(); // packageId -> version
        foreach (var kvp in packageGroups)
        {
            var packageId = kvp.Key;
            var version = kvp.Value.Version;
            

            if (string.IsNullOrEmpty(version))
            {
                version = await GetLatestPackageVersionAsync(packageId);
                if (version == null)
                {
                    AnsiConsole.MarkupLine($"    [yellow]⚠[/] Could not determine version for {packageId}");
                    continue;
                }
            }
            
            await ResolvePackageDependenciesRecursivelyAsync(packageId, version, targetFramework, allPackages);
        }

        AnsiConsole.MarkupLine($"    [dim]Total packages (including transitive): {allPackages.Count}[/]");


        foreach (var kvp in allPackages)
        {
            var packageId = kvp.Key;
            var version = kvp.Value;
            var packageKey = $"{packageId}/{version}";

            if (_acquiredPackages.Contains(packageKey))
            {
                continue; // Already downloaded
            }

            try
            {
                var packagePath = await DownloadPackageAsync(packageId, version);
                _acquiredPackages.Add(packageKey);


                var extractPath = Path.Combine(_workingDirectory, "nuget-refs", $"{packageId}.{version}");
                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);


                foreach (var folder in new[] { "lib", "ref" })
                {
                    var folderPath = Path.Combine(extractPath, folder);
                    if (Directory.Exists(folderPath))
                    {

                        var tfmFolders = Directory.GetDirectories(folderPath);
                        var bestMatch = SelectBestTfmFolder(tfmFolders, targetFramework);
                        
                        if (bestMatch != null)
                        {
                            var dlls = Directory.GetFiles(bestMatch, "*.dll", SearchOption.TopDirectoryOnly);
                            foreach (var dll in dlls)
                            {
                                var fileName = Path.GetFileName(dll);
                                if (!result.ContainsKey(fileName))
                                {
                                    result[fileName] = dll;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"    [yellow]⚠[/] Failed to download {packageId}: {ex.Message}");
            }
        }

        if (result.Count > 0)
        {
            AnsiConsole.MarkupLine($"    [green]✓[/] Extracted {result.Count} NuGet package assemblies");
        }

        return result;
    }

    /// <summary>
    /// Gets the latest version of a package from NuGet.
    /// </summary>
    private async Task<string?> GetLatestPackageVersionAsync(string packageId)
    {
        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            var versions = await resource.GetAllVersionsAsync(
                packageId,
                cache,
                _logger,
                CancellationToken.None);

            var version = versions
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .FirstOrDefault() ?? versions.OrderByDescending(v => v).FirstOrDefault();

            return version?.ToNormalizedString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively resolves package dependencies and adds them to the allPackages dictionary.
    /// </summary>
    private async Task ResolvePackageDependenciesRecursivelyAsync(
        string packageId, 
        string version, 
        string targetFramework,
        Dictionary<string, string> allPackages)
    {
        var packageKey = $"{packageId}/{version}";
        

        if (allPackages.ContainsKey(packageId))
        {
            return;
        }


        allPackages[packageId] = version;

        try
        {

            var packagePath = await DownloadPackageAsync(packageId, version);


            var extractPath = Path.Combine(_workingDirectory, "nuget-refs", $"{packageId}.{version}");
            if (!Directory.Exists(extractPath))
            {
                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, overwriteFiles: true);
            }


            var nuspecPath = Directory.GetFiles(extractPath, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (nuspecPath == null)
            {
                return; // No dependencies
            }

            var dependencies = ParseDependencies(nuspecPath, targetFramework);
            

            foreach (var (depId, depVersion) in dependencies)
            {
                await ResolvePackageDependenciesRecursivelyAsync(depId, depVersion, targetFramework, allPackages);
            }
        }
        catch (Exception)
        {

        }
    }

    /// <summary>
    /// Parses dependencies from a .nuspec file for the given target framework.
    /// </summary>
    private List<(string PackageId, string Version)> ParseDependencies(string nuspecPath, string targetFramework)
    {
        var dependencies = new List<(string, string)>();

        try
        {
            var doc = XDocument.Load(nuspecPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            
            var metadata = doc.Root?.Element(ns + "metadata");
            if (metadata == null)
            {
                return dependencies;
            }

            var dependenciesElement = metadata.Element(ns + "dependencies");
            if (dependenciesElement == null)
            {
                return dependencies;
            }


            var targetNuGetFramework = NuGetFramework.Parse(targetFramework);


            var groups = dependenciesElement.Elements(ns + "group").ToList();
            if (groups.Any())
            {

                var bestGroup = FindBestMatchingDependencyGroup(groups, ns, targetNuGetFramework);
                if (bestGroup != null)
                {
                    foreach (var dep in bestGroup.Elements(ns + "dependency"))
                    {
                        var id = dep.Attribute("id")?.Value;
                        var version = dep.Attribute("version")?.Value;
                        
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
                        {

                            var versionToUse = ParseVersionRange(version);
                            dependencies.Add((id, versionToUse));
                        }
                    }
                }
            }
            else
            {
                // No groups - dependencies apply to all frameworks
                foreach (var dep in dependenciesElement.Elements(ns + "dependency"))
                {
                    var id = dep.Attribute("id")?.Value;
                    var version = dep.Attribute("version")?.Value;
                    
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
                    {
                        var versionToUse = ParseVersionRange(version);
                        dependencies.Add((id, versionToUse));
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignore parsing errors
        }

        return dependencies;
    }

    /// <summary>
    /// Finds the best matching dependency group for the target framework.
    /// </summary>
    private XElement? FindBestMatchingDependencyGroup(List<XElement> groups, XNamespace ns, NuGetFramework targetFramework)
    {
        XElement? bestMatch = null;
        XElement? fallbackMatch = null;

        foreach (var group in groups)
        {
            var tfmAttr = group.Attribute("targetFramework")?.Value;
            
            if (string.IsNullOrEmpty(tfmAttr))
            {
                // No target framework means it applies to all
                fallbackMatch ??= group;
                continue;
            }

            var groupFramework = NuGetFramework.Parse(tfmAttr);
            
            // Exact match
            if (groupFramework.Equals(targetFramework))
            {
                return group;
            }

            // Compatible match
            if (DefaultCompatibilityProvider.Instance.IsCompatible(targetFramework, groupFramework))
            {
                bestMatch ??= group;
            }
        }

        return bestMatch ?? fallbackMatch;
    }

    /// <summary>
    /// Parses a NuGet version range and returns a specific version to use.
    /// </summary>
    private string ParseVersionRange(string versionString)
    {
        try
        {
            // Handle exact versions
            if (!versionString.Contains("[") && !versionString.Contains("(") && 
                !versionString.Contains("]") && !versionString.Contains(")"))
            {
                return versionString;
            }


            var range = VersionRange.Parse(versionString);
            
            // Use minimum version if specified
            if (range.MinVersion != null)
            {
                return range.MinVersion.ToNormalizedString();
            }

            // Use maximum version if specified
            if (range.MaxVersion != null)
            {
                return range.MaxVersion.ToNormalizedString();
            }

            // Default to the original string
            return versionString.Trim('[', ']', '(', ')');
        }
        catch
        {
            // If parsing fails, return the original string
            return versionString.Trim('[', ']', '(', ')');
        }
    }

    private async Task<Dictionary<string, string>> ExtractReferencesFromCachedPackageAsync(
        string packageId,
        string version,
        string targetFramework,
        List<MetadataReference> frameworkRefs)
    {
        var result = new Dictionary<string, string>();
        var extractPath = Path.Combine(_workingDirectory, "framework-refs", $"{packageId}.{version}");

        if (Directory.Exists(extractPath))
        {
            var refDir = Path.Combine(extractPath, "ref", targetFramework);
            if (Directory.Exists(refDir))
            {
                var dlls = Directory.GetFiles(refDir, "*.dll", SearchOption.TopDirectoryOnly);
                foreach (var dll in dlls)
                {
                    var fileName = Path.GetFileName(dll);
                    result[fileName] = dll;
                }
            }
        }

        return result;
    }

    private async Task<string> DownloadPackageAsync(string packageId, string? versionString)
    {
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

        var packagePath = Path.Combine(_workingDirectory, "packages", $"{packageId}.{version}.nupkg");
        var packageDir = Path.GetDirectoryName(packagePath)!;
        Directory.CreateDirectory(packageDir);

        if (File.Exists(packagePath))
        {
            return packagePath; // Already downloaded
        }

        await using var packageStream = File.Create(packagePath);
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

    private bool IsFrameworkAssembly(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        
        // Common framework assembly prefixes
        var frameworkPrefixes = new[]
        {
            "System.",
            "Microsoft.CSharp",
            "Microsoft.VisualBasic",
            "mscorlib",
            "netstandard",
            "WindowsBase",
            "PresentationCore",
            "PresentationFramework"
        };


        if (frameworkPrefixes.Any(prefix => 
            name.Equals(prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            // Exception: Microsoft.Extensions.* are typically NuGet packages, not framework
            if (name.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        return false;
    }

    private bool IsNuGetPackageReference(string fileName)
    {
        // NuGet packages are typically in paths like:
        // .nuget/packages/{package}/{version}/lib/{tfm}/{assembly}.dll
        var normalizedPath = fileName.Replace('\\', '/').ToLowerInvariant();
        

        if (normalizedPath.Contains("/.nuget/packages/") || normalizedPath.Contains("/packages/"))
        {
            return true;
        }

        // If it's just a filename (from PDB metadata), check if it's likely a NuGet package
        // by checking if it's NOT a framework assembly
        if (!Path.IsPathRooted(fileName) && !IsFrameworkAssembly(fileName))
        {
            return true;
        }

        return false;
    }

    private (string PackageId, string Version)? ExtractPackageInfoFromPath(string filePath)
    {
        // Expected format: .../packages/{packageId}/{version}/lib/{tfm}/{assembly}.dll
        var normalizedPath = filePath.Replace('\\', '/');
        var packagesIndex = normalizedPath.LastIndexOf("/packages/", StringComparison.OrdinalIgnoreCase);
        
        if (packagesIndex >= 0)
        {
            var afterPackages = normalizedPath.Substring(packagesIndex + "/packages/".Length);
            var parts = afterPackages.Split('/');
            
            if (parts.Length >= 2)
            {
                return (parts[0], parts[1]);
            }
        }

        // If it's just a filename (from PDB metadata), infer package ID from assembly name
        // For most packages, the package ID matches the assembly name (without .dll)
        if (!Path.IsPathRooted(filePath) && filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Path.GetFileNameWithoutExtension(filePath);
            // Return without version - we'll need to look up the latest or find it from dependencies
            return (assemblyName, null!); // Version will be resolved later
        }

        return null;
    }

    private string? SelectBestTfmFolder(string[] tfmFolders, string targetFramework)
    {
        // Simple heuristic: prefer exact match, then closest match
        var tfmNames = tfmFolders.Select(Path.GetFileName).ToList();
        
        // Exact match
        if (tfmNames.Any(t => t != null && t.Equals(targetFramework, StringComparison.OrdinalIgnoreCase)))
        {
            return tfmFolders.First(f => Path.GetFileName(f).Equals(targetFramework, StringComparison.OrdinalIgnoreCase));
        }

        // Return first folder as fallback (could be improved with proper TFM compatibility logic)
        return tfmFolders.FirstOrDefault();
    }

    private string? GetDotNetRoot()
    {

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        // Platform-specific defaults
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            dotnetRoot = Path.Combine(programFiles, "dotnet");
        }
        else
        {
            // Common Unix locations
            var locations = new[] { "/usr/local/share/dotnet", "/usr/share/dotnet", "/opt/dotnet" };
            dotnetRoot = locations.FirstOrDefault(Directory.Exists);
        }

        return dotnetRoot;
    }
}
