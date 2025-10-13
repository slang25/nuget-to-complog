using System.Text.Json;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Infrastructure.SourceDownload;

/// <summary>
/// Downloads source files from HTTP URLs using Source Link mappings.
/// </summary>
public class HttpSourceFileDownloader : ISourceFileDownloader
{
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public HttpSourceFileDownloader(IFileSystemService fileSystem, IConsoleWriter console)
    {
        _fileSystem = fileSystem;
        _console = console;
    }

    public async Task<int> DownloadSourceFilesAsync(
        List<SourceFileInfo> sourceFiles,
        string sourceLinkJson,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var mappings = ParseSourceLinkMappings(sourceLinkJson);
        if (mappings.Count == 0)
        {
            return 0;
        }

        _console.MarkupLine("  [cyan]Downloading from Source Link URLs...[/]");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "NuGetToCompLog/1.0");

        var downloadTasks = new List<Task<(bool success, string fileName)>>();
        var semaphore = new SemaphoreSlim(5); // Limit concurrent downloads

        foreach (var sourceFile in sourceFiles.Where(sf => !sf.IsEmbedded))
        {
            var url = MapSourceLinkUrl(sourceFile.Path, mappings);
            if (url == null) continue;

            downloadTasks.Add(DownloadSourceFileAsync(
                httpClient,
                semaphore,
                url,
                sourceFile.Path,
                destinationDirectory,
                cancellationToken));
        }

        var results = await Task.WhenAll(downloadTasks);
        var successCount = results.Count(r => r.success);

        if (successCount > 0)
        {
            _console.MarkupLine($"  [green]✓[/] Downloaded {successCount} source files from repository");
        }

        return successCount;
    }

    private Dictionary<string, string> ParseSourceLinkMappings(string? sourceLinkJson)
    {
        var mappings = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(sourceLinkJson))
        {
            return mappings;
        }

        try
        {
            using var doc = JsonDocument.Parse(sourceLinkJson);
            if (doc.RootElement.TryGetProperty("documents", out var documents))
            {
                foreach (var prop in documents.EnumerateObject())
                {
                    mappings[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"  [yellow]⚠[/] Failed to parse Source Link JSON: [dim]{ex.Message}[/]");
        }

        return mappings;
    }

    private string? MapSourceLinkUrl(string localPath, Dictionary<string, string> mappings)
    {
        var normalizedPath = localPath.Replace('\\', '/');

        foreach (var (pattern, urlTemplate) in mappings)
        {
            var normalizedPattern = pattern.Replace('\\', '/');

            var wildcardIndex = normalizedPattern.IndexOf('*');
            if (wildcardIndex < 0) continue;

            var prefix = normalizedPattern.Substring(0, wildcardIndex);

            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = normalizedPath.Substring(prefix.Length);
                var url = urlTemplate.Replace("*", relativePath);
                return url;
            }
        }

        return null;
    }

    private async Task<(bool success, string fileName)> DownloadSourceFileAsync(
        HttpClient httpClient,
        SemaphoreSlim semaphore,
        string url,
        string localPath,
        string sourcesDir,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                SaveSourceFile(sourcesDir, localPath, content);

                return (true, Path.GetFileName(localPath));
            }
            else
            {
                return (false, Path.GetFileName(localPath));
            }
        }
        catch
        {
            return (false, Path.GetFileName(localPath));
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void SaveSourceFile(string sourcesDir, string originalPath, string content)
    {
        var normalizedPath = originalPath.Replace('\\', '/').TrimStart('/');

        // Strip common prefixes
        var patterns = new[] { "_/Src/", "_/src/", "Src/", "src/" };
        foreach (var pattern in patterns)
        {
            var idx = normalizedPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                normalizedPath = normalizedPath.Substring(idx + pattern.Length);
                break;
            }
        }

        // Skip first directory component (package name)
        var parts = normalizedPath.Split('/');
        if (parts.Length > 1)
        {
            normalizedPath = string.Join("/", parts.Skip(1));
        }

        var fullPath = Path.Combine(sourcesDir, normalizedPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (directory != null)
        {
            _fileSystem.CreateDirectory(directory);
        }

        _fileSystem.WriteAllTextAsync(fullPath, content).Wait();
    }
}
