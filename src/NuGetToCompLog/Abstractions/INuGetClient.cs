using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for downloading NuGet packages.
/// </summary>
public interface INuGetClient
{
    /// <summary>
    /// Downloads a NuGet package to a local path.
    /// </summary>
    /// <param name="package">Package identity to download.</param>
    /// <param name="destinationPath">Local path where package should be saved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to the downloaded package file.</returns>
    Task<string> DownloadPackageAsync(PackageIdentity package, string destinationPath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Downloads a symbols package (.snupkg) to a local path.
    /// </summary>
    /// <param name="package">Package identity to download symbols for.</param>
    /// <param name="destinationPath">Local path where symbols package should be saved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Path to the downloaded symbols package, or null if not available.</returns>
    Task<string?> DownloadSymbolsPackageAsync(PackageIdentity package, string destinationPath, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the latest stable version of a package, or latest prerelease if no stable exists.
    /// </summary>
    /// <param name="packageId">Package ID to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest version string.</returns>
    Task<string> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all available versions of a package.
    /// </summary>
    /// <param name="packageId">Package ID to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available version strings.</returns>
    Task<List<string>> GetAllVersionsAsync(string packageId, CancellationToken cancellationToken = default);
}
