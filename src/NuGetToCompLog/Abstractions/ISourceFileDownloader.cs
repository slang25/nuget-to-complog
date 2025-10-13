using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for downloading source files from various sources.
/// </summary>
public interface ISourceFileDownloader
{
    /// <summary>
    /// Downloads source files using Source Link mappings.
    /// </summary>
    /// <param name="sourceFiles">List of source files to download.</param>
    /// <param name="sourceLinkJson">Source Link JSON configuration.</param>
    /// <param name="destinationDirectory">Directory to save downloaded files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of files successfully downloaded.</returns>
    Task<int> DownloadSourceFilesAsync(
        List<SourceFileInfo> sourceFiles,
        string sourceLinkJson,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
