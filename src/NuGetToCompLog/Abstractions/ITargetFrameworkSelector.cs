using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for selecting the best target framework from available options.
/// </summary>
public interface ITargetFrameworkSelector
{
    /// <summary>
    /// Selects the best target framework from a list of assemblies grouped by TFM.
    /// </summary>
    /// <param name="assemblies">List of assembly paths.</param>
    /// <param name="extractPath">Base extraction path to determine TFM from path structure.</param>
    /// <returns>List of assemblies for the selected TFM and the TFM identifier.</returns>
    (List<string> Assemblies, string? TargetFramework) SelectBestTargetFramework(
        List<string> assemblies,
        string extractPath);
}
