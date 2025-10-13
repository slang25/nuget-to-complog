using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Services.References;

/// <summary>
/// Wrapper service that implements IReferenceResolver using the existing
/// ReferenceAssemblyAcquisitionService. This allows gradual migration.
/// </summary>
public class ReferenceResolverService : IReferenceResolver
{
    private readonly ReferenceAssemblyAcquisitionService _acquisitionService;

    public ReferenceResolverService(string workingDirectory)
    {
        _acquisitionService = new ReferenceAssemblyAcquisitionService(workingDirectory);
    }

    public async Task<Dictionary<string, string>> AcquireAllReferencesAsync(
        List<MetadataReference> references,
        string targetFramework,
        CancellationToken cancellationToken = default)
    {
        return await _acquisitionService.AcquireAllReferencesAsync(references, targetFramework);
    }
}
