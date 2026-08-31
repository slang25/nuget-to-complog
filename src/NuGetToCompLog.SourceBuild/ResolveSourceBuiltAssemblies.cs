using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace NuGetToCompLog.SourceBuild;

/// <summary>
/// Finds the assets this project resolved from packages it consumes as source builds, and returns
/// the item edits that put locally compiled assemblies in their place.
///
/// Configuration is one attribute on a PackageReference. Everything else the substitution needs -
/// which version the graph settled on, which lib folder this target framework picked, what the
/// assembly is called - is already in the items ResolvePackageAssets produced, so it is read from
/// there rather than recorded somewhere that could disagree with the build.
/// </summary>
public sealed class ResolveSourceBuiltAssemblies : Task
{
    [Required]
    public string CacheRoot { get; set; } = "";

    /// <summary>ResolvedCompileFileDefinitions, as ResolvePackageAssets produced them.</summary>
    public ITaskItem[] CompileItems { get; set; } = [];

    /// <summary>RuntimeCopyLocalItems, as ResolvePackageAssets produced them.</summary>
    public ITaskItem[] RuntimeItems { get; set; } = [];

    /// <summary>The project's PackageReference items; those marked SourceBuild="true" are used.</summary>
    public ITaskItem[] PackageReferences { get; set; } = [];

    /// <summary>
    /// Extra package ids to source-build, for packages the project does not reference directly.
    /// A licensed library is often reached through something else, and there is no PackageReference
    /// on which to put the marker.
    /// </summary>
    public ITaskItem[] SourceBuildPackages { get; set; } = [];

    /// <summary>
    /// Report what is not cached instead of failing on it. The targets run the task in this mode
    /// first to find out what to build, build it, then run it again for real - so a cold cache is
    /// a thing to fix rather than a thing to complain about.
    /// </summary>
    public bool ReportMissingOnly { get; set; }

    /// <summary>
    /// Assets with nothing usable cached, as "id/version/pathInPackage" - exactly the form the
    /// tool takes on its command line.
    /// </summary>
    [Output] public ITaskItem[] AssetsToBuild { get; private set; } = [];

    [Output] public ITaskItem[] CompileItemsToRemove { get; private set; } = [];
    [Output] public ITaskItem[] CompileItemsToAdd { get; private set; } = [];
    [Output] public ITaskItem[] RuntimeItemsToRemove { get; private set; } = [];
    [Output] public ITaskItem[] RuntimeItemsToAdd { get; private set; } = [];

    /// <summary>What was substituted, for a build log at normal verbosity.</summary>
    [Output] public ITaskItem[] Substitutions { get; private set; } = [];

    public override bool Execute()
    {
        var requested = RequestedPackageIds();
        if (requested.Count == 0)
        {
            return true;
        }

        var compile = Substitute(CompileItems, requested, rewriteHintPath: true);
        var runtime = Substitute(RuntimeItems, requested, rewriteHintPath: false);

        if (!ReportMissingOnly)
        {
            ReportPackagesThatResolvedNothing(requested, compile.Matched, runtime.Matched);
        }

        AssetsToBuild = Distinct(compile.Missing.Concat(runtime.Missing));
        CompileItemsToRemove = compile.Removed.ToArray();
        CompileItemsToAdd = compile.Added.ToArray();
        RuntimeItemsToRemove = runtime.Removed.ToArray();
        RuntimeItemsToAdd = runtime.Added.ToArray();
        Substitutions = Distinct(compile.Substitutions.Concat(runtime.Substitutions));

        return !Log.HasLoggedErrors;
    }

    private static ITaskItem[] Distinct(IEnumerable<ITaskItem> items) => items
        .GroupBy(i => i.ItemSpec, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToArray();

    private HashSet<string> RequestedPackageIds()
    {
        var ids = PackageReferences
            .Where(r => string.Equals(r.GetMetadata("SourceBuild"), "true", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ItemSpec)
            .Concat(SourceBuildPackages.Select(p => p.ItemSpec));
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A package asked for by name that contributed no assembly at all. Usually a typo or a
    /// package that only ships analyzers or build assets; either way the project would build
    /// against something other than what it asked for, without saying so.
    /// </summary>
    private void ReportPackagesThatResolvedNothing(
        HashSet<string> requested, HashSet<string> matchedCompile, HashSet<string> matchedRuntime)
    {
        foreach (var id in requested.Where(id => !matchedCompile.Contains(id) && !matchedRuntime.Contains(id)))
        {
            Log.LogError(null, "NTCL1001", null, null, 0, 0, 0, 0,
                "{0} is marked SourceBuild=\"true\" but this project resolves no assembly from it. " +
                "Analyzer and build-only packages contribute no lib assembly, so there is nothing to " +
                "source-build; check the package id otherwise.", id);
        }
    }

    private (List<ITaskItem> Removed, List<ITaskItem> Added, List<ITaskItem> Substitutions,
             List<ITaskItem> Missing, HashSet<string> Matched) Substitute(
        ITaskItem[] items, HashSet<string> requested, bool rewriteHintPath)
    {
        var removed = new List<ITaskItem>();
        var added = new List<ITaskItem>();
        var substitutions = new List<ITaskItem>();
        var missing = new List<ITaskItem>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var packageId = item.GetMetadata("NuGetPackageId");
            if (string.IsNullOrEmpty(packageId) || !requested.Contains(packageId))
            {
                continue;
            }

            var version = item.GetMetadata("NuGetPackageVersion");
            var pathInPackage = item.GetMetadata("PathInPackage");
            var cached = CachedAssembly.For(CacheRoot, packageId, version, pathInPackage);
            if (cached == null)
            {
                // A RID-specific or otherwise unusual asset. Not something a source build covers,
                // and not something to fail the build over either.
                continue;
            }

            matched.Add(packageId);

            if (!cached.Exists || !cached.IsIntact())
            {
                if (ReportMissingOnly)
                {
                    missing.Add(new TaskItem($"{packageId}/{version}/{pathInPackage}"));
                    continue;
                }
                Log.LogError(null, "NTCL1002", null, null, 0, 0, 0, 0,
                    "{0} {1} is consumed as a source build but no assembly could be built or cached at {2}.",
                    packageId, version, cached.Path);
                continue;
            }

            // Copy rather than rebuild the item: PathInPackage and NuGetPackageId have to survive
            // or GenerateDepsFile stops writing the package's own deps.json entry.
            var replacement = new TaskItem(cached.Path);
            item.CopyMetadataTo(replacement);

            // ResolveAssemblyReferences prefers a reference's HintPath over its item spec, so an
            // inherited HintPath resolves straight back to the published binary - the substitution
            // then works everywhere except the one place that decides what the compiler reads.
            if (rewriteHintPath && !string.IsNullOrEmpty(item.GetMetadata("HintPath")))
            {
                replacement.SetMetadata("HintPath", cached.Path);
            }

            removed.Add(item);
            added.Add(replacement);
            substitutions.Add(new TaskItem($"{packageId} {version} ({pathInPackage})"));
        }

        return (removed, added, substitutions, missing, matched);
    }
}
