using System.Reflection;
using System.Xml.Linq;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.Reconstruction;
using NuGetToCompLog.Services.SourceBuild;
using NuGetToCompLog.Services.Swap;
using NuGetToCompLog.Services.Verify;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Builds a package from its own source and caches the result, so a build can use that assembly
/// instead of the one the package shipped.
///
/// This is deliberately not <c>swap</c>. Swap ejects readable source into the repository so it can
/// be edited; this ejects nothing. The source exists only inside a complog in a machine-local
/// cache, and what the build consumes is the compiled result. The point is a library that is
/// unchanged but compiled here, so the artifact has to be genuinely self-compiled: the ledger gate
/// below refuses to proceed on source that was recovered from the binary rather than from the
/// project's own sources.
///
/// Which assemblies to build is not decided here. A build knows the package id, the version its
/// graph resolved and the exact asset it picked out of the package, so the MSBuild targets pass
/// all three in and this only has to build what it is told. Marking a PackageReference
/// SourceBuild="true" is the whole of the configuration; there is nothing else to keep in step
/// with it.
/// </summary>
public class SourceBuildCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly ComplogRebuilder _rebuilder;
    private readonly IConsoleWriter _console;

    public SourceBuildCommandHandler(
        PackageAnalysisPipeline pipeline,
        ComplogRebuilder rebuilder,
        IConsoleWriter console)
    {
        _pipeline = pipeline;
        _rebuilder = rebuilder;
        _console = console;
    }

    public async Task<int> HandleAsync(
        string? packageId,
        string? assets = null,
        string? project = null,
        bool fetchCompiler = false,
        bool allowDivergent = false,
        bool runGenerators = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(assets))
            {
                return await BuildAssetsAsync(assets, fetchCompiler, allowDivergent, runGenerators, cancellationToken);
            }

            if (packageId == null)
            {
                _console.MarkupLine("[red]✗[/] Name the package to source-build");
                return 1;
            }

            return MarkProject(packageId, project);
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]✗[/] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Marks a project's PackageReference so its next build uses a source build. This writes two
    /// lines into the project file and nothing else - no cache is populated here, because the
    /// build knows which assets it resolves and will ask for exactly those.
    /// </summary>
    private int MarkProject(string packageId, string? project)
    {
        var projectPath = PackageReferenceSwapper.FindProjectFile(project, Directory.GetCurrentDirectory());
        var projectDoc = XDocument.Load(projectPath);

        if (PackageReferenceSwapper.FindPackageReferences(projectDoc, packageId).Count == 0)
        {
            _console.MarkupLine($"[red]✗[/] No PackageReference to [cyan]{packageId}[/] in [dim]{projectPath}[/]");
            var existing = PackageReferenceSwapper.ListPackageReferences(projectDoc);
            if (existing.Count > 0)
            {
                _console.MarkupLine($"[dim]   Package references in this project: {string.Join(", ", existing)}[/]");
            }
            _console.MarkupLine($"[dim]   A package reached only transitively can be source-built too - add a[/]");
            _console.MarkupLine($"[dim]   PackageReference for it, or a <SourceBuildPackage Include=\"{packageId}\" /> item.[/]");
            return 1;
        }

        if (PackageReferenceSwapper.EnsureBuildPackageReference(projectPath, BuildPackageId, BuildPackageVersion))
        {
            _console.MarkupLine($"  [green]✓[/] Added [cyan]{BuildPackageId}[/] [yellow]{BuildPackageVersion}[/]");
        }

        var marked = PackageReferenceSwapper.MarkSourceBuild(projectPath, packageId);
        _console.MarkupLine(marked > 0
            ? $"  [green]✓[/] Marked {marked} PackageReference item{(marked == 1 ? "" : "s")} SourceBuild=\"true\""
            : "  [dim]Already marked SourceBuild=\"true\"[/]");

        _console.WriteLine();
        _console.WritePanel(
            "Source Build Ready",
            $"[green]{packageId} will be built from its own source on the next build.[/]\n\n" +
            "[dim]Nothing is ejected - the source stays sealed in a complog beside the cached assembly.[/]\n" +
            $"[dim]Cache:[/] [cyan]{SourceBuildCache.DefaultRoot()}[/]\n\n" +
            "[dim]Build as usual:[/]\n  [yellow]dotnet build[/]\n\n" +
            "[dim]To go back to the published binary for one build:[/]\n" +
            "  [yellow]dotnet build -p:NuGetToCompLogDisableSourceBuild=true[/]",
            "Green");
        return 0;
    }

    /// <summary>
    /// Builds the assets the caller names, each as "id/version/pathInPackage". This is the entry
    /// point the MSBuild targets use: the build has already resolved which package version and
    /// which lib folder are in play, and reading that back out of a project file would only be a
    /// worse guess at what it already knows.
    /// </summary>
    private async Task<int> BuildAssetsAsync(
        string assets,
        bool fetchCompiler,
        bool allowDivergent,
        bool runGenerators,
        CancellationToken cancellationToken)
    {
        var requested = assets
            .Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0)
            .Select(ParseAsset)
            .ToList();
        if (requested.Count == 0)
        {
            _console.MarkupLine("[red]✗[/] No assets to build");
            return 1;
        }

        var cache = new SourceBuildCache();
        foreach (var asset in requested)
        {
            // Parallel builds of a solution all reach a cold cache at once. Hold the entry while
            // building it, and re-check after waiting: whoever held it first has done the work.
            using var entryLock = await cache.LockAsync(
                asset.PackageId, asset.Version, asset.PackageTargetFramework, cancellationToken);
            if (cache.Contains(asset.PackageId, asset.Version, asset.PackageTargetFramework, asset.AssemblyFileName))
            {
                _console.MarkupLine($"  [green]✓[/] {asset.PackageId} [yellow]{asset.Version}[/] [dim]already cached[/]");
                continue;
            }

            if (await BuildAsync(asset, cache, fetchCompiler, allowDivergent, runGenerators, cancellationToken) == null)
            {
                return 1;
            }
        }

        return 0;
    }

    private static ResolvedPackageAsset ParseAsset(string asset)
    {
        // "Serilog/4.0.0/lib/net8.0/Serilog.dll" - the path inside the package contains slashes
        // of its own, so only the first two separators are structural.
        var parts = asset.Split('/');
        if (parts.Length < 4)
        {
            throw new InvalidOperationException(
                $"Malformed asset '{asset}'. Expected <packageId>/<version>/<pathInPackage>.");
        }
        return new ResolvedPackageAsset(parts[0], parts[1], string.Join("/", parts.Skip(2)));
    }

    /// <summary>
    /// Reconstructs, rebuilds, checks and caches one asset. Returns null when any step refused,
    /// having already said why.
    /// </summary>
    private async Task<SourceBuildProvenance?> BuildAsync(
        ResolvedPackageAsset asset,
        SourceBuildCache cache,
        bool fetchCompiler,
        bool allowDivergent,
        bool runGenerators,
        CancellationToken cancellationToken)
    {
        _console.WriteLine();
        _console.MarkupLine($"[yellow]Reconstructing {asset.PackageId} {asset.Version} ({asset.PackageTargetFramework})[/]");

        var result = await _pipeline.AnalyzeAsync(
            asset.PackageId, asset.Version, asset.AssemblyFileName, asset.PackageTargetFramework, cancellationToken);
        if (result == null || result.CompilerArgsFile == null)
        {
            _console.MarkupLine($"[red]✗[/] No compiler arguments could be extracted from {asset.PackageId} {asset.Version}");
            _console.MarkupLine("[dim]   A source build needs the package's portable PDB, embedded or from its symbol package.[/]");
            return null;
        }

        if (!PassesSourceProvenanceGate(result))
        {
            return null;
        }

        var complogPath = await CompLogFileCreator.CreateCompLogFileAsync(
            asset.PackageId, result.Package.Version, result.WorkingDirectory, result.WorkingDirectory,
            result.SelectedTfm, result.SelectedAssemblies, result.Ledger, runGenerators);
        if (!File.Exists(complogPath))
        {
            _console.MarkupLine("[red]✗[/] Complog creation failed");
            return null;
        }

        var originalAssembly = result.SelectedAssemblies.First();
        var rebuild = await _rebuilder.RebuildAsync(
            result, complogPath, Path.GetFileNameWithoutExtension(originalAssembly),
            Path.Combine(result.WorkingDirectory, "sourcebuild-export"),
            fetchCompiler, cancellationToken);

        var ledgerPath = Path.Combine(
            result.WorkingDirectory, $"{asset.PackageId}.{result.Package.Version}.reconstruction.json");
        await result.Ledger.SaveAsync(ledgerPath);

        if (!rebuild.Success)
        {
            _console.MarkupLine($"[red]✗[/] {rebuild.Failure}");
            return null;
        }

        var (equivalence, comparison, surface) = Classify(originalAssembly, rebuild);
        if (!ReportEquivalence(asset, rebuild, comparison, surface, equivalence, allowDivergent))
        {
            return null;
        }

        var provenance = await cache.StoreAsync(
            asset.PackageId, asset.Version, asset.PackageTargetFramework,
            rebuild.RebuiltAssembly!, rebuild.RebuiltPdb, complogPath, ledgerPath,
            equivalence, comparison.DerivedDifferences, rebuild.CompilerVersion?.Split('+')[0],
            rebuild.CompilerWasExact, rebuild.RuntimeWasExact,
            result.Ledger.Outlook.ToString().ToLowerInvariant(), surface?.SurfaceSize);

        _console.MarkupLine($"  [green]✓[/] Cached [dim]{cache.DirectoryFor(asset.PackageId, asset.Version, asset.PackageTargetFramework)}[/]");
        return provenance;
    }

    /// <summary>
    /// Refuses to source-build from source the tool recovered out of the shipped assembly.
    ///
    /// This is the gate that keeps the artifact honest. What makes a self-compiled binary a
    /// legitimate substitute for a licensed one is that it was compiled from the project's
    /// source - the thing the OSI licence grants. Decompiled source is derived from the binary
    /// instead, so a build containing it is not the independently compiled binary the maintenance
    /// fee agreement carves out, whatever the bytes look like afterwards. Substitutions in other
    /// categories are fine: a public-signing stand-in or an inferred compiler flag changes how the
    /// compilation is configured, not where its code came from.
    /// </summary>
    private bool PassesSourceProvenanceGate(PackageExtractionResult result)
    {
        var substitutedSource = result.Ledger.Entries
            .Where(e => e.Category == ReconstructionLedger.CategorySource &&
                        e.Evidence == InputEvidence.Substituted)
            .ToList();
        if (substitutedSource.Count == 0)
        {
            return true;
        }

        var total = substitutedSource.Sum(e => e.Count);
        _console.MarkupLine($"[red]✗[/] Refusing to source-build: {total} source " +
                            $"document{(total == 1 ? "" : "s")} could not be recovered from the package's own sources");
        foreach (var entry in substitutedSource.Take(5))
        {
            _console.MarkupLine($"  [dim]• {entry.Name}: {entry.Detail}[/]");
        }
        _console.MarkupLine("[dim]   Source recovered from the shipped assembly is derived from the binary, not compiled[/]");
        _console.MarkupLine("[dim]   from source, so the result would not be an independently compiled binary.[/]");
        _console.MarkupLine("[dim]   `nuget-to-complog eject` will still produce it for inspection.[/]");
        return false;
    }

    /// <summary>
    /// Decides what the rebuild proved.
    ///
    /// A byte comparison is only evidence when the exact compiler and runtime ran: any other
    /// Roslyn compiling the same source produces different bytes routinely, so under a different
    /// toolchain "the bytes differ" says nothing about whether the library is the same. The
    /// question that survives the toolchain is whether the public surface and the referenced
    /// assembly identities match, so that is what gets asked instead - the same standard as
    /// building the library from its repository yourself, which is the alternative the licence
    /// names.
    /// </summary>
    private static (BinaryEquivalence Equivalence, ComparisonResult Comparison, SurfaceComparison? Surface) Classify(
        string originalAssembly, RebuildOutcome rebuild)
    {
        var comparison = BinaryDiffClassifier.CompareAssemblies(originalAssembly, rebuild.RebuiltAssembly!);

        if (comparison.ExactMatch)
        {
            return (BinaryEquivalence.Identical, comparison, null);
        }
        if (comparison.DerivedOnly)
        {
            return (BinaryEquivalence.ContentEquivalent, comparison, null);
        }
        if (rebuild.ToolchainWasExact)
        {
            // The bytes should have matched and did not, so something about the reconstructed
            // inputs is wrong. The surface would be the weaker answer to a question already
            // answered by the stronger one.
            return (BinaryEquivalence.Divergent, comparison, null);
        }

        var surface = AssemblySurfaceComparer.Compare(originalAssembly, rebuild.RebuiltAssembly!);
        return (surface.Matches ? BinaryEquivalence.ApiEquivalent : BinaryEquivalence.Divergent, comparison, surface);
    }

    private bool ReportEquivalence(
        ResolvedPackageAsset asset,
        RebuildOutcome rebuild,
        ComparisonResult comparison,
        SurfaceComparison? surface,
        BinaryEquivalence equivalence,
        bool allowDivergent)
    {
        _console.WriteLine();
        switch (equivalence)
        {
            case BinaryEquivalence.Identical:
                _console.MarkupLine($"[green]✓ Byte-for-byte identical[/] to the assembly {asset.PackageId} shipped");
                return true;

            case BinaryEquivalence.ContentEquivalent:
                // For a signed package this is the ceiling, not a near miss: the Authenticode
                // signature covers bytes only the publisher's key can produce.
                _console.MarkupLine("[green]✓ Content-equivalent[/] to the assembly the package shipped");
                _console.MarkupLine("[dim]  Every content byte matches; these fields are derived from the signing key and PDB:[/]");
                foreach (var difference in comparison.DerivedDifferences)
                {
                    _console.MarkupLine($"    [dim]• {difference}[/]");
                }
                return true;

            case BinaryEquivalence.ApiEquivalent:
                _console.MarkupLine("[green]✓ Compiled from the package's source with this machine's toolchain[/]");
                ReportToolchain(rebuild);
                _console.MarkupLine("[dim]  Different compilers emit different bytes from identical source, so the check is[/]");
                _console.MarkupLine("[dim]  what a consumer can actually observe:[/]");
                _console.MarkupLine($"    [green]✓[/] [dim]all {surface!.SurfaceSize:N0} public types and members match the shipped assembly[/]");
                _console.MarkupLine("    [green]✓[/] [dim]every referenced assembly identity matches[/]");
                return true;

            default:
                if (rebuild.ToolchainWasExact)
                {
                    _console.MarkupLine("[red]✗ The rebuild differs from the shipped assembly in ways that are not derived fields:[/]");
                    foreach (var difference in comparison.RealDifferences.Take(10))
                    {
                        _console.MarkupLine($"    [dim]• {difference}[/]");
                    }
                    _console.MarkupLine("[dim]   The exact compiler and runtime ran, so the bytes should have matched.[/]");
                }
                else
                {
                    _console.MarkupLine($"[red]✗ The rebuild is not the same library as the assembly {asset.PackageId} shipped:[/]");
                    ReportSurfaceDifferences(surface!);
                }

                if (allowDivergent)
                {
                    _console.MarkupLine("[yellow]⚠[/] Continuing because --allow-divergent was passed; " +
                                        "this assembly is not known to behave like the one it replaces");
                    return true;
                }
                _console.MarkupLine("[dim]   A source build has to be the same library, or substituting it changes behaviour[/]");
                _console.MarkupLine("[dim]   silently. Run `nuget-to-complog verify` to see the causes, or build with[/]");
                _console.MarkupLine("[dim]   -p:NuGetToCompLogAllowDivergent=true to accept the difference deliberately.[/]");
                return false;
        }
    }

    private void ReportToolchain(RebuildOutcome rebuild)
    {
        if (!rebuild.CompilerWasExact && rebuild.CompilerVersion != null)
        {
            _console.MarkupLine($"  [dim]Compiler: this machine's, not the {rebuild.CompilerVersion.Split('+')[0]} the package used[/]");
        }
        if (!rebuild.RuntimeWasExact)
        {
            _console.MarkupLine("  [dim]Runtime:  the one that hosted the original compiler is not installed[/]");
        }
    }

    private void ReportSurfaceDifferences(SurfaceComparison surface)
    {
        foreach (var missing in surface.MissingFromRebuild.Take(5))
        {
            _console.MarkupLine($"    [dim]• missing from the rebuild: {missing.Replace("[", "[[").Replace("]", "]]")}[/]");
        }
        foreach (var added in surface.AddedByRebuild.Take(5))
        {
            _console.MarkupLine($"    [dim]• only in the rebuild: {added.Replace("[", "[[").Replace("]", "]]")}[/]");
        }
        foreach (var reference in surface.ReferenceDifferences.Take(5))
        {
            _console.MarkupLine($"    [dim]• {reference}[/]");
        }

        var total = surface.MissingFromRebuild.Count + surface.AddedByRebuild.Count + surface.ReferenceDifferences.Count;
        if (total > 15)
        {
            _console.MarkupLine($"    [dim]...and {total - 15} more[/]");
        }
    }

    /// <summary>The build-only package that performs the substitution at build time.</summary>
    private const string BuildPackageId = "NuGetToCompLog.SourceBuild";

    /// <summary>
    /// Referenced at this tool's own version, since the two are released together and the asset
    /// command line is the contract between them.
    /// </summary>
    private static string BuildPackageVersion =>
        typeof(SourceBuildCommandHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.4.0";
}
