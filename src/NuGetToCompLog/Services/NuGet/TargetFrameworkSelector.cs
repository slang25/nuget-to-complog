using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Services.NuGet;

/// <summary>
/// Service for selecting the best target framework from available assemblies.
/// </summary>
public class TargetFrameworkSelector : ITargetFrameworkSelector
{
    public (List<string> Assemblies, string? TargetFramework) SelectBestTargetFramework(
        List<string> assemblies,
        string extractPath)
    {
        if (assemblies.Count == 0)
            return (assemblies, null);

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
        {
            var tfm = groupedByTfm[0].Key;
            return (assemblies, tfm != "unknown" ? tfm : null);
        }

        // Select the best TFM based on priority
        var bestTfmGroup = groupedByTfm
            .Select(g => new
            {
                Tfm = g.Key,
                Info = TargetFrameworkInfo.Parse(g.Key),
                Assemblies = g.ToList()
            })
            .OrderByDescending(x => x.Info.Priority)
            .ThenByDescending(x => x.Info.MajorVersion)
            .ThenByDescending(x => x.Info.MinorVersion)
            .First();

        return (bestTfmGroup.Assemblies, bestTfmGroup.Tfm != "unknown" ? bestTfmGroup.Tfm : null);
    }
}
