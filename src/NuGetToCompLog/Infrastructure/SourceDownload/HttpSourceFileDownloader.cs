using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Infrastructure.Http;
using NuGetToCompLog.Services.Pdb;

namespace NuGetToCompLog.Infrastructure.SourceDownload;

/// <summary>
/// Downloads source files from HTTP URLs using Source Link mappings.
/// </summary>
public class HttpSourceFileDownloader : ISourceFileDownloader
{
    /// <summary>
    /// Maximum number of source files fetched concurrently. SourceLink hosts (raw.githubusercontent.com
    /// and friends) are CDN-backed and multiplex happily over HTTP/2, so a higher cap than the old
    /// value of 5 cuts the download phase markedly without risking throttling.
    /// </summary>
    private const int MaxConcurrentDownloads = 16;

    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SourceFileDecompilerService? _decompiler;

    public HttpSourceFileDownloader(
        IFileSystemService fileSystem,
        IConsoleWriter console,
        IHttpClientFactory httpClientFactory,
        SourceFileDecompilerService? decompiler = null)
    {
        _fileSystem = fileSystem;
        _console = console;
        _httpClientFactory = httpClientFactory;
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

        // Resolve every file to its SourceLink URL up front; skip ones with no mapping.
        var workItems = new List<(string Url, string SourcePath, string LocalPath)>();
        foreach (var sourceFile in nonEmbeddedFiles)
        {
            var url = MapSourceLinkUrl(sourceFile.Path, mappings);
            if (url == null) continue;
            var localPath = sourceFile.LocalPath != null
                ? Path.Combine(destinationDirectory, sourceFile.LocalPath)
                : GetDestinationPath(sourceFile.Path, destinationDirectory);
            workItems.Add((url, sourceFile.Path, localPath));
        }

        // Bounded channel + a fixed pool of workers caps concurrency: the channel's capacity gives
        // the producer backpressure, and the worker count is the hard ceiling on in-flight downloads.
        var workerCount = Math.Min(MaxConcurrentDownloads, Math.Max(1, workItems.Count));
        var channel = Channel.CreateBounded<(string Url, string SourcePath, string LocalPath)>(
            new BoundedChannelOptions(MaxConcurrentDownloads)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            });

        var results = new ConcurrentBag<(bool Success, string SourceFilePath)>();

        // One client for the whole batch; its handler is pooled by the factory. HttpClient is
        // thread-safe for concurrent sends, so all workers share this instance.
        var httpClient = _httpClientFactory.CreateClient(HttpClientNames.SourceDownload);

        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    var result = await DownloadSourceFileAsync(
                        httpClient,
                        item.Url,
                        item.SourcePath,
                        item.LocalPath,
                        cancellationToken);
                    results.Add(result);
                }
            }, cancellationToken);
        }

        foreach (var item in workItems)
        {
            await channel.Writer.WriteAsync(item, cancellationToken);
        }
        channel.Writer.Complete();

        await Task.WhenAll(workers);

        var successCount = results.Count(r => r.Success);
        var failedFiles = results
            .Where(r => !r.Success)
            .Select(r => r.SourceFilePath)
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
            var localPathBySource = workItems.ToDictionary(w => w.SourcePath, w => w.LocalPath);
            var actuallyMissing = new List<string>();
            foreach (var failedFile in failedFiles)
            {
                var destinationPath = localPathBySource.TryGetValue(failedFile, out var mapped)
                    ? mapped
                    : GetDestinationPath(failedFile, destinationDirectory);
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

    private async Task<(bool Success, string SourceFilePath)> DownloadSourceFileAsync(
        HttpClient httpClient,
        string url,
        string sourcePath,
        string localPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                // Save the exact bytes the server returned. Decoding to a string and re-encoding
                // can drop the BOM or change the encoding, which breaks PDB checksum verification.
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                var directory = Path.GetDirectoryName(localPath);
                if (directory != null)
                {
                    _fileSystem.CreateDirectory(directory);
                }
                await _fileSystem.WriteAllBytesAsync(localPath, content);

                return (true, sourcePath);
            }
            else
            {
                return (false, sourcePath);
            }
        }
        catch
        {
            return (false, sourcePath);
        }
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
