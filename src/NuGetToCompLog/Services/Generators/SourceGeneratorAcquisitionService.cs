using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;

namespace NuGetToCompLog.Services.Generators;

/// <summary>
/// Which analyzer assemblies to attach to the rebuild and which manifest documents they
/// replace. AnalyzerFileNames are paths relative to sources/analyzers/ - one subdirectory per
/// generator payload - and list every DLL of the payload, not just the generator assembly:
/// a complog only stores the analyzer references it sees on the command line, so an
/// unreferenced dependency would be missing from the exported build and the generator would
/// fail to load.
/// </summary>
public record GeneratorPlan(
    List<string> AnalyzerFileNames,
    HashSet<string> GeneratedLocalPaths,
    string GeneratedFilesBaseDir,
    Dictionary<string, string> GlobalOptions);

/// <summary>
/// Replaces generator-produced source documents with the actual source generator run.
///
/// Passing generator outputs as plain files can never reproduce the original PDB exactly:
/// csc embeds generated trees itself with the generator's own checksum algorithm (SHA-1 by
/// SourceText default, regardless of /checksumalgorithm) and its own compression path.
/// This service finds the generator assembly the original build used (the analyzed package's
/// own analyzers, the exact targeting pack, or a NuGet package named after the generator),
/// then proves in-process - via CSharpGeneratorDriver against the same sources and references -
/// that it regenerates the extracted documents byte-for-byte before swapping it in. On any
/// mismatch the plain-file behavior is kept.
/// </summary>
public static class SourceGeneratorAcquisitionService
{
    // [{Project}/...]obj/{Config}/{Tfm}/{GeneratorAssembly}/{GeneratorType}/{file}.cs
    // (the local path may keep leading project segments when the pathmap root sits above the
    // project directory, e.g. "/_/src/" for FluentValidation)
    private static readonly Regex GeneratedDocPattern = new(
        @"^(?<base>(?:[^/]+/)*obj/[^/]+/[^/]+)/(?<asm>[^/]+)/(?<type>[^/]+)/[^/]+$",
        RegexOptions.Compiled);

    private static readonly Regex GeneratedCodeVersionPattern = new(
        @"GeneratedCode(?:Attribute)?\s*\(\s*""[^""]*""\s*,\s*""(?<ver>[^""]+)""",
        RegexOptions.Compiled);

    public static async Task<GeneratorPlan?> TryPlanAsync(
        string workingDirectory,
        SourceManifest manifest,
        Dictionary<string, string> argsDict,
        Dictionary<string, string> acquiredReferences,
        string assemblyName)
    {
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        var byAssembly = new Dictionary<string, List<(string LocalPath, string TypeName, string BaseDir)>>();
        foreach (var doc in manifest.Documents)
        {
            var match = GeneratedDocPattern.Match(doc.LocalPath.Replace('\\', '/'));
            if (!match.Success)
            {
                continue;
            }
            if (!byAssembly.TryGetValue(match.Groups["asm"].Value, out var list))
            {
                byAssembly[match.Groups["asm"].Value] = list = [];
            }
            list.Add((doc.LocalPath, match.Groups["type"].Value, match.Groups["base"].Value));
        }

        if (byAssembly.Count == 0)
        {
            return null;
        }

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[yellow]Acquiring source generators:[/]");

        Compilation? validationCompilation = null;
        var allGeneratedPaths = byAssembly.Values.SelectMany(v => v.Select(d => d.LocalPath))
            .ToHashSet(StringComparer.Ordinal);
        var analyzersDir = Path.Combine(sourcesDir, "analyzers");
        var plan = new GeneratorPlan([], [], byAssembly.Values.First().First().BaseDir, new Dictionary<string, string>());
        var anyFailed = false;

        // One payload directory per source directory the DLLs came from, so generators shipped
        // side by side in a package share a payload while generators needing different versions
        // of the same dependency stay separated. Dependencies are listed after every generator
        // assembly (see AttachPayload) to keep generator run order matching document order.
        var payloadDirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var dependencyFileNames = new List<string>();

        // Copies the candidate directory into its payload directory on first use and returns
        // the payload's name; records every non-generator DLL as part of the analyzer payload.
        string AttachPayload(string candidateDir, string generatorAssembly)
        {
            if (payloadDirs.TryGetValue(candidateDir, out var existing))
            {
                return existing;
            }

            var payloadName = generatorAssembly;
            var payloadDir = Path.Combine(analyzersDir, payloadName);
            Directory.CreateDirectory(payloadDir);
            foreach (var dll in Directory.GetFiles(candidateDir, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(dll);
                File.Copy(dll, Path.Combine(payloadDir, fileName), overwrite: true);
                // Generator assemblies are referenced by their own attach step, in document
                // order; referencing one twice would run it twice and duplicate its output.
                if (!byAssembly.ContainsKey(Path.GetFileNameWithoutExtension(fileName)))
                {
                    dependencyFileNames.Add(Path.Combine(payloadName, fileName));
                }
            }

            payloadDirs[candidateDir] = payloadName;
            return payloadName;
        }

        GeneratorPlan? Finish()
        {
            if (plan.AnalyzerFileNames.Count == 0)
            {
                return null;
            }
            plan.AnalyzerFileNames.AddRange(dependencyFileNames);
            return plan;
        }

        foreach (var (generatorAssembly, docs) in byAssembly)
        {
            var expected = new Dictionary<(string Type, string File), string>();
            string? generatedCodeVersion = null;
            var missing = false;
            foreach (var (localPath, typeName, _) in docs)
            {
                var fullPath = Path.Combine(sourcesDir, localPath);
                if (!File.Exists(fullPath))
                {
                    missing = true;
                    break;
                }
                var content = await File.ReadAllTextAsync(fullPath);
                expected[(typeName, Path.GetFileName(localPath))] = content;
                generatedCodeVersion ??= GeneratedCodeVersionPattern.Match(content) is { Success: true } m
                    ? m.Groups["ver"].Value
                    : null;
            }
            if (missing)
            {
                anyFailed = true;
                continue;
            }

            var pinnedVersion = await TryFindPinnedVersionAsync(workingDirectory, manifest, generatorAssembly);
            if (pinnedVersion != null)
            {
                AnsiConsole.MarkupLine($"  [dim]Repository pins {generatorAssembly} at {pinnedVersion}[/]");
            }

            var candidateDirs = await LocateGeneratorAsync(
                workingDirectory, generatorAssembly, generatedCodeVersion, pinnedVersion,
                argsDict.GetValueOrDefault("compiler-version"));
            if (candidateDirs.Count == 0)
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] Generator {generatorAssembly} could not be located - keeping its outputs as plain sources");
                anyFailed = true;
                continue;
            }

            validationCompilation ??= BuildValidationCompilation(
                sourcesDir, manifest, allGeneratedPaths, argsDict, acquiredReferences, assemblyName);
            if (validationCompilation == null)
            {
                return Finish();
            }

            // Try each candidate dll, first with no analyzer options, then with options inferred
            // from the expected output (e.g. PolySharp's include-list, whose entries are exactly
            // the generated file names). An inference is only used if it validates byte-for-byte.
            var optionSets = new List<Dictionary<string, string>> { new() };
            if (InferGeneratorOptions(generatorAssembly, docs) is { } inferredOptions)
            {
                optionSets.Add(inferredOptions);
            }

            var attached = false;
            foreach (var candidateDir in candidateDirs)
            {
                var dllPath = Path.Combine(candidateDir, generatorAssembly + ".dll");
                foreach (var options in optionSets)
                {
                    var (ok, why) = ValidateGenerator(dllPath, validationCompilation, argsDict, options, expected);
                    if (!ok)
                    {
                        AnsiConsole.MarkupLine($"  [dim]{generatorAssembly} ({Path.GetFileName(candidateDir)}{(options.Count > 0 ? ", inferred options" : "")}): {why}[/]");
                        continue;
                    }

                    var payloadName = AttachPayload(candidateDir, generatorAssembly);
                    plan.AnalyzerFileNames.Add(Path.Combine(payloadName, generatorAssembly + ".dll"));
                    foreach (var (key, value) in options)
                    {
                        plan.GlobalOptions[key] = value;
                    }
                    foreach (var (localPath, _, _) in docs)
                    {
                        plan.GeneratedLocalPaths.Add(localPath);
                    }
                    AnsiConsole.MarkupLine($"  [green]✓[/] {generatorAssembly} regenerates all {docs.Count} document(s) byte-for-byte - attaching as analyzer");
                    attached = true;
                    break;
                }
                if (attached)
                {
                    break;
                }
            }

            if (!attached)
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] Generator {generatorAssembly} output does not match the original - keeping plain sources");
                anyFailed = true;
            }
        }

        // All-or-nothing: generated trees are appended after all sources in generator run
        // order, so attaching one generator while another's outputs stay plain files reorders
        // the documents (and with them type/attribute emission) relative to the original.
        if (anyFailed && plan.AnalyzerFileNames.Count > 0)
        {
            AnsiConsole.MarkupLine("  [yellow]⚠[/] Not all generators could be matched - keeping plain sources for all of them");
            return null;
        }

        return Finish();
    }

    private static readonly HttpClient Http = new();

    /// <summary>
    /// The repository's own build files record which generator package version the original
    /// build used (generators are PrivateAssets so the nuspec never sees them). Fetch the
    /// likely project files at the exact Source Link commit and read the PackageReference.
    /// </summary>
    private static async Task<string?> TryFindPinnedVersionAsync(
        string workingDirectory,
        SourceManifest manifest,
        string generatorAssembly)
    {
        try
        {
            var sourceLinkPath = Path.Combine(workingDirectory, "source-link.json");
            if (!File.Exists(sourceLinkPath) || manifest.PathMapRoot == null)
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(sourceLinkPath));
            var mapping = doc.RootElement.GetProperty("documents").EnumerateObject().FirstOrDefault();
            var keyPrefix = mapping.Name.TrimEnd('*');
            var urlBase = mapping.Value.GetString()?.TrimEnd('*');
            if (urlBase == null || !manifest.PathMapRoot.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            // Project directory relative to the repo root, from a non-generated document's path
            // (the pathmap root may sit above the project, e.g. "/_/src/").
            var projectDir = manifest.Documents
                .Select(d => d.DocumentPath.Replace('\\', '/'))
                .Where(p => p.StartsWith(manifest.PathMapRoot, StringComparison.OrdinalIgnoreCase) && p.Contains("/obj/"))
                .Select(p => p[keyPrefix.Length..p.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase)])
                .FirstOrDefault();

            var candidateFiles = new List<string>();
            if (projectDir != null)
            {
                candidateFiles.Add($"{projectDir}/{projectDir.Split('/')[^1]}.csproj");
                for (var dir = projectDir; ; dir = dir[..Math.Max(dir.LastIndexOf('/'), 0)])
                {
                    var prefix = dir.Length > 0 ? dir + "/" : "";
                    candidateFiles.Add($"{prefix}Directory.Build.props");
                    candidateFiles.Add($"{prefix}Directory.Packages.props");
                    if (dir.Length == 0 || !dir.Contains('/'))
                    {
                        if (dir.Length > 0)
                        {
                            candidateFiles.Add("Directory.Build.props");
                            candidateFiles.Add("Directory.Packages.props");
                        }
                        break;
                    }
                }
            }

            // The generator package is usually named like the assembly (or a prefix of it).
            var idPattern = string.Join("|",
                EnumeratePackageIdCandidates(generatorAssembly).Select(Regex.Escape));
            var referencePattern = new Regex(
                $@"PackageReference\s[^>]*Include\s*=\s*""(?:{idPattern})""[^>]*Version\s*=\s*""([^""]+)""",
                RegexOptions.IgnoreCase);

            foreach (var file in candidateFiles.Distinct())
            {
                try
                {
                    var response = await Http.GetAsync(urlBase + file);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }
                    var content = await response.Content.ReadAsStringAsync();
                    if (referencePattern.Match(content) is { Success: true } match)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch (HttpRequestException)
                {
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static IEnumerable<string> EnumeratePackageIdCandidates(string generatorAssembly)
    {
        var id = generatorAssembly;
        while (!string.IsNullOrEmpty(id))
        {
            yield return id;
            var lastDot = id.LastIndexOf('.');
            if (lastDot <= 0)
            {
                yield break;
            }
            id = id[..lastDot];
        }
    }

    /// <summary>
    /// Infers the analyzer options the original build likely passed, from the shape of its
    /// output. Returns null when no inference applies for this generator.
    /// </summary>
    private static Dictionary<string, string>? InferGeneratorOptions(
        string generatorAssembly,
        List<(string LocalPath, string TypeName, string BaseDir)> docs)
    {
        // PolySharp generates every applicable polyfill by default; a curated original output
        // means the build set PolySharpIncludeGeneratedTypes. The generated file names are
        // exactly the polyfilled type full names.
        if (generatorAssembly.StartsWith("PolySharp", StringComparison.OrdinalIgnoreCase))
        {
            var types = docs
                .Select(d => Path.GetFileName(d.LocalPath))
                .Where(f => f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                .Select(f => f[..^".g.cs".Length]);
            return new Dictionary<string, string>
            {
                ["build_property.PolySharpIncludeGeneratedTypes"] = string.Join(";", types),
            };
        }

        return null;
    }

    /// <summary>
    /// Candidate directories (each containing the generator dll plus its dependencies), best
    /// first: the analyzed package's own analyzers, the targeting pack matching the version in
    /// the GeneratedCode attribute, then same-named NuGet packages.
    /// </summary>
    private static async Task<List<string>> LocateGeneratorAsync(
        string workingDirectory,
        string generatorAssembly,
        string? generatedCodeVersion,
        string? pinnedVersion,
        string? compilerVersion)
    {
        var candidates = new List<string>();
        var extractDirBase = Path.Combine(workingDirectory, "generators", generatorAssembly);

        // 1. The analyzed package ships its own generator (e.g. Microsoft.Extensions.Logging.Abstractions).
        var ownAnalyzers = Path.Combine(workingDirectory, "extracted", "analyzers");
        if (Directory.Exists(ownAnalyzers))
        {
            var dll = SelectAnalyzerDll(
                Directory.GetFiles(ownAnalyzers, generatorAssembly + ".dll", SearchOption.AllDirectories),
                compilerVersion);
            if (dll != null)
            {
                candidates.Add(Path.GetDirectoryName(dll)!);
            }
        }

        // 2. SDK generators live in the targeting pack; the GeneratedCode attribute version
        //    (e.g. "8.0.13.10607") names the exact servicing release.
        if (generatedCodeVersion != null && Version.TryParse(generatedCodeVersion, out var parsed))
        {
            var packVersion = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
            candidates.AddRange(await ExtractCandidatesFromNuGetAsync(
                "Microsoft.NETCore.App.Ref", [packVersion], generatorAssembly, compilerVersion,
                extractDirBase, workingDirectory));
        }

        // 3. A NuGet package named after the generator assembly, trimming trailing segments
        //    (PolySharp.SourceGenerators -> PolySharp). Without a recorded generator version,
        //    recent stable versions are tried newest-first; validation picks the one whose
        //    output matches.
        var versions = new List<string>();
        if (pinnedVersion != null)
        {
            versions.Add(pinnedVersion);
        }
        if (generatedCodeVersion != null && Version.TryParse(generatedCodeVersion, out var v))
        {
            versions.Add($"{v.Major}.{v.Minor}.{v.Build}");
            versions.Add(generatedCodeVersion);
        }
        versions.Add("latest");

        var packageId = generatorAssembly;
        while (!string.IsNullOrEmpty(packageId))
        {
            var dirs = await ExtractCandidatesFromNuGetAsync(
                packageId, versions, generatorAssembly, compilerVersion,
                extractDirBase, workingDirectory);
            if (dirs.Count > 0)
            {
                candidates.AddRange(dirs);
                break;
            }
            var lastDot = packageId.LastIndexOf('.');
            packageId = lastDot > 0 ? packageId[..lastDot] : null!;
        }

        return candidates;
    }

    private static async Task<List<string>> ExtractCandidatesFromNuGetAsync(
        string packageId,
        IReadOnlyList<string> versionPreferences,
        string generatorAssembly,
        string? compilerVersion,
        string extractDirBase,
        string workingDirectory,
        int maxLatest = 12)
    {
        var results = new List<string>();
        try
        {
            var cache = new SourceCacheContext();
            var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
            var available = (await resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, CancellationToken.None)).ToList();
            if (available.Count == 0)
            {
                return results;
            }

            var versions = new List<NuGetVersion>();
            foreach (var preference in versionPreferences)
            {
                if (preference == "latest")
                {
                    versions.AddRange(available.Where(x => !x.IsPrerelease).OrderByDescending(x => x).Take(maxLatest));
                }
                else if (available.FirstOrDefault(x => x.ToNormalizedString().Equals(preference, StringComparison.OrdinalIgnoreCase)) is { } exact)
                {
                    versions.Add(exact);
                }
            }

            foreach (var version in versions.Distinct())
            {
                var dir = await TryExtractVersionAsync(
                    resource, cache, packageId, version, generatorAssembly, compilerVersion,
                    Path.Combine(extractDirBase, $"pkg-{packageId}-{version}"), workingDirectory);
                if (dir != null)
                {
                    results.Add(dir);
                }
            }
        }
        catch
        {
        }
        return results;
    }

    private static async Task<string?> TryExtractVersionAsync(
        FindPackageByIdResource resource,
        SourceCacheContext cache,
        string packageId,
        NuGetVersion version,
        string generatorAssembly,
        string? compilerVersion,
        string extractDir,
        string workingDirectory)
    {
        try
        {
            var nupkgPath = Path.Combine(workingDirectory, "packages", $"{packageId}.{version}.nupkg");
            if (!File.Exists(nupkgPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(nupkgPath)!);
                await using var stream = File.Create(nupkgPath);
                if (!await resource.CopyNupkgToStreamAsync(packageId, version, stream, cache, NullLogger.Instance, CancellationToken.None))
                {
                    return null;
                }
            }

            using var archive = ZipFile.OpenRead(nupkgPath);
            var dllEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("analyzers/", StringComparison.OrdinalIgnoreCase) &&
                            e.FullName.EndsWith("/" + generatorAssembly + ".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var chosen = SelectAnalyzerDll(dllEntries.Select(e => e.FullName), compilerVersion);
            if (chosen == null)
            {
                return null;
            }

            // Extract the chosen dll's whole directory so the generator's dependencies come along.
            var sourceDir = chosen[..chosen.LastIndexOf('/')];
            Directory.CreateDirectory(extractDir);
            foreach (var entry in archive.Entries.Where(e =>
                         e.FullName.StartsWith(sourceDir + "/", StringComparison.OrdinalIgnoreCase) &&
                         e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                entry.ExtractToFile(Path.Combine(extractDir, Path.GetFileName(entry.FullName)), overwrite: true);
            }
            return File.Exists(Path.Combine(extractDir, generatorAssembly + ".dll")) ? extractDir : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Analyzer packages ship per-Roslyn-version builds (analyzers/roslyn4.4/...); pick the
    /// highest one the original compiler can load.
    /// </summary>
    private static string? SelectAnalyzerDll(IEnumerable<string> paths, string? compilerVersion)
    {
        var compilerMajorMinor = new Version(99, 99);
        var versionMatch = compilerVersion != null ? Regex.Match(compilerVersion, @"^(\d+)\.(\d+)") : null;
        if (versionMatch is { Success: true })
        {
            compilerMajorMinor = new Version(int.Parse(versionMatch.Groups[1].Value), int.Parse(versionMatch.Groups[2].Value));
        }

        return paths
            .Select(p =>
            {
                var m = Regex.Match(p, @"roslyn(\d+)\.(\d+)", RegexOptions.IgnoreCase);
                var v = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
                return (Path: p, RoslynVersion: v);
            })
            .Where(x => x.RoslynVersion <= compilerMajorMinor)
            .OrderByDescending(x => x.RoslynVersion)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static Compilation? BuildValidationCompilation(
        string sourcesDir,
        SourceManifest manifest,
        HashSet<string> generatedPaths,
        Dictionary<string, string> argsDict,
        Dictionary<string, string> acquiredReferences,
        string assemblyName)
    {
        try
        {
            var parseOptions = BuildParseOptions(argsDict);
            var trees = new List<SyntaxTree>();
            foreach (var doc in manifest.Documents)
            {
                if (generatedPaths.Contains(doc.LocalPath))
                {
                    continue;
                }
                var path = Path.Combine(sourcesDir, doc.LocalPath);
                if (!File.Exists(path))
                {
                    continue;
                }
                trees.Add(CSharpSyntaxTree.ParseText(
                    SourceText.From(File.ReadAllText(path)), parseOptions, path: doc.LocalPath));
            }

            var nullable = argsDict.GetValueOrDefault("nullable") switch
            {
                "Enable" => NullableContextOptions.Enable,
                "Warnings" => NullableContextOptions.Warnings,
                "Annotations" => NullableContextOptions.Annotations,
                _ => NullableContextOptions.Disable,
            };
            return CSharpCompilation.Create(
                assemblyName,
                trees,
                acquiredReferences.Values.Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: nullable));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠[/] Cannot build validation compilation: {ex.Message.Replace("[", "[[").Replace("]", "]]")}");
            return null;
        }
    }

    private static CSharpParseOptions BuildParseOptions(Dictionary<string, string> argsDict)
    {
        var parseOptions = CSharpParseOptions.Default;
        if (argsDict.TryGetValue("language-version", out var langVersion) &&
            LanguageVersionFacts.TryParse(langVersion, out var parsedVersion))
        {
            parseOptions = parseOptions.WithLanguageVersion(parsedVersion);
        }
        if (argsDict.TryGetValue("define", out var defines))
        {
            parseOptions = parseOptions.WithPreprocessorSymbols(
                defines.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries));
        }
        return parseOptions;
    }

    /// <summary>
    /// Runs every generator in the assembly against the validation compilation and requires the
    /// produced sources to be exactly the expected (type, file) → content set - no drift, no
    /// extras (an extra hint name would become an extra PDB document).
    /// </summary>
    private static (bool Ok, string Why) ValidateGenerator(
        string dllPath,
        Compilation compilation,
        Dictionary<string, string> argsDict,
        Dictionary<string, string> globalOptions,
        Dictionary<(string Type, string File), string> expected)
    {
        try
        {
            var directory = Path.GetDirectoryName(dllPath)!;
            // Each candidate (possibly the same assembly name at different versions) loads into
            // its own context; Microsoft.CodeAnalysis still unifies via the default context.
            var loadContext = new System.Runtime.Loader.AssemblyLoadContext($"generator-{Guid.NewGuid():N}", isCollectible: true);
            loadContext.Resolving += (context, name) =>
            {
                var candidate = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                var generators = new List<ISourceGenerator>();
                Type?[] types;
                string? loadFailure = null;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                    loadFailure = ex.LoaderExceptions.FirstOrDefault(e => e != null)?.Message;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract)
                    {
                        continue;
                    }
                    if (typeof(IIncrementalGenerator).IsAssignableFrom(type))
                    {
                        generators.Add(((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator());
                    }
                    else if (typeof(ISourceGenerator).IsAssignableFrom(type))
                    {
                        generators.Add((ISourceGenerator)Activator.CreateInstance(type)!);
                    }
                }
                if (generators.Count == 0)
                {
                    var detail = loadFailure != null ? $" ({loadFailure})" : $" ({types.Count(t => t != null)} types inspected)";
                    return (false, "no generator types found" + detail.Replace("[", "[[").Replace("]", "]]"));
                }

                var driver = CSharpGeneratorDriver.Create(
                    generators.ToArray(),
                    parseOptions: BuildParseOptions(argsDict),
                    optionsProvider: new InMemoryAnalyzerConfigOptionsProvider(globalOptions));
                var runResult = driver.RunGenerators(compilation).GetRunResult();

                var produced = new Dictionary<(string, string), string>();
                foreach (var result in runResult.Results)
                {
                    var typeName = result.Generator.GetGeneratorType().FullName!;
                    foreach (var source in result.GeneratedSources)
                    {
                        produced[(typeName, Path.GetFileName(source.HintName))] = source.SourceText.ToString();
                    }
                }

                if (produced.Count != expected.Count)
                {
                    var extra = produced.Keys.Select(k => k.Item2)
                        .Except(expected.Keys.Select(k => k.File), StringComparer.OrdinalIgnoreCase);
                    return (false, $"produced {produced.Count} source(s), original had {expected.Count} " +
                                   $"(extra: {string.Join(", ", extra)})");
                }
                foreach (var ((type, file), content) in expected)
                {
                    if (!produced.TryGetValue((type, file), out var actual))
                    {
                        return (false, $"did not produce {file}");
                    }
                    if (actual != content)
                    {
                        // Leave the regenerated text next to the candidate dll for diffing.
                        var dumpPath = Path.Combine(directory, file + ".regenerated");
                        File.WriteAllText(dumpPath, actual);
                        return (false, $"content differs for {file}");
                    }
                }
                return (true, "");
            }
            finally
            {
                loadContext.Unload();
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Replace("[", "[[").Replace("]", "]]"));
        }
    }

    private sealed class InMemoryAnalyzerConfigOptionsProvider : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider
    {
        private readonly InMemoryOptions _global;

        public InMemoryAnalyzerConfigOptionsProvider(Dictionary<string, string> values) => _global = new InMemoryOptions(values);

        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GlobalOptions => _global;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;
        public override Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _global;

        private sealed class InMemoryOptions : Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;

            public InMemoryOptions(Dictionary<string, string> values) =>
                _values = new Dictionary<string, string>(values, KeyComparer);

            public override bool TryGetValue(string key, out string value) =>
                _values.TryGetValue(key, out value!);
        }
    }
}
