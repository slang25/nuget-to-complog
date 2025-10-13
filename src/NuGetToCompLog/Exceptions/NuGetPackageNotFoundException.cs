namespace NuGetToCompLog.Exceptions;

/// <summary>
/// Exception thrown when a NuGet package cannot be found.
/// </summary>
public class NuGetPackageNotFoundException : Exception
{
    public string PackageId { get; }
    public string? Version { get; }
    
    public NuGetPackageNotFoundException(string packageId, string? version)
        : base($"NuGet package '{packageId}' {(version != null ? $"version '{version}' " : "")}not found")
    {
        PackageId = packageId;
        Version = version;
    }
    
    public NuGetPackageNotFoundException(string packageId, string? version, Exception innerException)
        : base($"NuGet package '{packageId}' {(version != null ? $"version '{version}' " : "")}not found", innerException)
    {
        PackageId = packageId;
        Version = version;
    }
}
