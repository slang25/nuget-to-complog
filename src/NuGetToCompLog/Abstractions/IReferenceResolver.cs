using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for resolving and acquiring reference assemblies.
/// </summary>
public interface IReferenceResolver
{
    /// <summary>
    /// Acquires all reference assemblies needed for compilation.
    /// </summary>
    /// <param name="references">List of metadata references from PDB.</param>
    /// <param name="targetFramework">Target framework moniker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping file names to local paths.</returns>
    Task<Dictionary<string, string>> AcquireAllReferencesAsync(
        List<MetadataReference> references,
        string targetFramework,
        CancellationToken cancellationToken = default);
}
