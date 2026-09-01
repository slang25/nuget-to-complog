namespace NuGetToCompLog.Services.SourceBuild;

/// <summary>
/// One assembly a build resolved out of a package, identified the way the build sees it.
/// </summary>
/// <param name="PackageId">The package's id with the casing NuGet restored it under.</param>
/// <param name="Version">The version the graph resolved to, which may be higher than any project pins.</param>
/// <param name="PathInPackage">The asset's path inside the package, e.g. lib/net8.0/Serilog.dll.</param>
public record ResolvedPackageAsset(string PackageId, string Version, string PathInPackage)
{
    /// <summary>The lib folder's framework - the compilation that has to be rebuilt.</summary>
    public string PackageTargetFramework
    {
        get
        {
            var parts = PathInPackage.Split('/');
            return parts.Length >= 3 ? parts[^2] : parts[0];
        }
    }

    public string AssemblyFileName => PathInPackage.Split('/')[^1];
}
