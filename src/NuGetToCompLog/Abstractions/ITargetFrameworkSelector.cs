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
    /// <param name="requiredTargetFramework">
    /// When set, the only acceptable TFM. A consumer resolves one specific lib folder out of a
    /// package, and reconstructing a different one would describe a compilation it never uses, so
    /// callers that know which folder is in play say so instead of taking the best guess. Returns
    /// no assemblies when the package has no such folder, which the caller reports rather than
    /// silently falling back.
    /// </param>
    /// <returns>List of assemblies for the selected TFM and the TFM identifier.</returns>
    (List<string> Assemblies, string? TargetFramework) SelectBestTargetFramework(
        List<string> assemblies,
        string extractPath,
        string? requiredTargetFramework = null);
}
