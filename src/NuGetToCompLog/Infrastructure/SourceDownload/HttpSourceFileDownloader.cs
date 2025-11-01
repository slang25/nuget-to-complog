using System.Text.Json;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.Pdb;

namespace NuGetToCompLog.Infrastructure.SourceDownload;

/// <summary>
/// Downloads source files from HTTP URLs using Source Link mappings.
/// </summary>
public class HttpSourceFileDownloader : ISourceFileDownloader
{
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;
    private readonly SourceFileDecompilerService? _decompiler;

    public HttpSourceFileDownloader(
        IFileSystemService fileSystem, 
        IConsoleWriter console,
        SourceFileDecompilerService? decompiler = null)
    {
        _fileSystem = fileSystem;
        _console = console;
        _decompiler = decompiler;
    }

    public async Task<int> DownloadSourceFilesAsync(
        List<SourceFileInfo> sourceFiles,
        string sourceLinkJson,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return await DownloadSourceFilesAsync(
            sourceFiles,
            sourceLinkJson,
            destinationDirectory,
            assemblyPath: null,
            pdbMetadataReader: null,
            cancellationToken);
    }

    public async Task<int> DownloadSourceFilesAsync(
        List<SourceFileInfo> sourceFiles,
        string sourceLinkJson,
        string destinationDirectory,
        string? assemblyPath,
        System.Reflection.Metadata.MetadataReader? pdbMetadataReader,
        CancellationToken cancellationToken = default)
    {
        var mappings = ParseSourceLinkMappings(sourceLinkJson);
        var nonEmbeddedFiles = sourceFiles.Where(sf => !sf.IsEmbedded).ToList();
        
        if (mappings.Count == 0)
        {
            // No SourceLink mappings, check if we should decompile missing files
            if (nonEmbeddedFiles.Count > 0 && assemblyPath != null && _decompiler != null)
            {
                var missingFiles = nonEmbeddedFiles.Select(sf => sf.Path).ToList();
                var decompiledCount = await _decompiler.DecompileMissingFilesAsync(
                    assemblyPath,
                    missingFiles,
                    pdbMetadataReader,
                    destinationDirectory,
                    cancellationToken);
                
                if (decompiledCount > 0)
                {
                    _console.MarkupLine($"  [yellow]⚠[/] {missingFiles.Count} source files not available via SourceLink");
                    _console.MarkupLine($"  [cyan]→[/] Decompiled {decompiledCount} missing file(s) from assembly");
                }
                
                return decompiledCount;
            }
            return 0;
        }

        _console.MarkupLine("  [cyan]Downloading from Source Link URLs...[/]");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "NuGetToCompLog/1.0");

        var downloadTasks = new List<Task<(bool success, string sourceFilePath)>>();
        var semaphore = new SemaphoreSlim(5); // Limit concurrent downloads

        foreach (var sourceFile in nonEmbeddedFiles)
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
        var failedFiles = results
            .Where(r => !r.success)
            .Select(r => r.sourceFilePath)
            .ToList();

        if (successCount > 0)
        {
            _console.MarkupLine($"  [green]✓[/] Downloaded {successCount} source files from repository");
        }

        // Handle missing files via decompilation
        if (failedFiles.Count > 0 && assemblyPath != null && _decompiler != null)
        {
            _console.MarkupLine($"  [yellow]⚠[/] {failedFiles.Count} source file(s) could not be downloaded via SourceLink");
            
            // Check which files are actually missing (not downloaded successfully)
            var actuallyMissing = new List<string>();
            foreach (var failedFile in failedFiles)
            {
                var destinationPath = GetDestinationPath(failedFile, destinationDirectory);
                if (!File.Exists(destinationPath))
                {
                    actuallyMissing.Add(failedFile);
                }
            }

            if (actuallyMissing.Count > 0)
            {
                _console.MarkupLine($"  [cyan]→[/] Attempting to decompile {actuallyMissing.Count} missing file(s) from assembly...");
                
                var decompiledCount = await _decompiler.DecompileMissingFilesAsync(
                    assemblyPath,
                    actuallyMissing,
                    pdbMetadataReader,
                    destinationDirectory,
                    cancellationToken);
                
                if (decompiledCount > 0)
                {
                    _console.MarkupLine($"  [green]✓[/] Successfully decompiled {decompiledCount} missing file(s)");
                }
                else if (decompiledCount < actuallyMissing.Count)
                {
                    _console.MarkupLine($"  [yellow]⚠[/] Could not decompile all missing files ({decompiledCount}/{actuallyMissing.Count} succeeded)");
                }
            }
        }
        else if (failedFiles.Count > 0)
        {
            _console.MarkupLine($"  [yellow]⚠[/] {failedFiles.Count} source file(s) could not be downloaded and decompilation is not available");
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

    private async Task<(bool success, string sourceFilePath)> DownloadSourceFileAsync(
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

                return (true, localPath);
            }
            else
            {
                return (false, localPath);
            }
        }
        catch
        {
            return (false, localPath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void SaveSourceFile(string sourcesDir, string originalPath, string content)
    {
        var fullPath = GetDestinationPath(originalPath, sourcesDir);
        var directory = Path.GetDirectoryName(fullPath);

        if (directory != null)
        {
            _fileSystem.CreateDirectory(directory);
        }

        _fileSystem.WriteAllTextAsync(fullPath, content).Wait();
    }

    private string GetDestinationPath(string sourceFilePath, string destinationDirectory)
    {
        var normalizedPath = sourceFilePath.Replace('\\', '/').TrimStart('/');

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

        return Path.Combine(destinationDirectory, normalizedPath);
    }
}
