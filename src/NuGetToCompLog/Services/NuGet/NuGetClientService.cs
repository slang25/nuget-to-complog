using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Exceptions;

namespace NuGetToCompLog.Services.NuGet;

/// <summary>
/// Service for downloading NuGet packages from nuget.org.
/// </summary>
public class NuGetClientService : INuGetClient
{
    private readonly string _sourceUrl;
    private readonly ILogger _logger;

    public NuGetClientService(string sourceUrl = "https://api.nuget.org/v3/index.json")
    {
        _sourceUrl = sourceUrl;
        _logger = NullLogger.Instance;
    }

    public async Task<string> DownloadPackageAsync(
        PackageIdentity package,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3(_sourceUrl);
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);

        var version = NuGetVersion.Parse(package.Version);
        var packagePath = Path.Combine(destinationPath, package.FileName);

        await using var packageStream = File.Create(packagePath);
        var success = await resource.CopyNupkgToStreamAsync(
            package.Id,
            version,
            packageStream,
            cache,
            _logger,
            cancellationToken);

        if (!success)
        {
            throw new NuGetPackageNotFoundException(package.Id, package.Version);
        }

        return packagePath;
    }

    public async Task<string?> DownloadSymbolsPackageAsync(
        PackageIdentity package,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var snupkgPath = Path.Combine(destinationPath, package.SymbolsFileName);

        // Try multiple symbol package sources
        var snupkgUrls = new[]
        {
            $"https://api.nuget.org/v3-flatcontainer/{package.Id.ToLowerInvariant()}/{package.Version.ToLowerInvariant()}/{package.Id.ToLowerInvariant()}.{package.Version.ToLowerInvariant()}.snupkg",
            // v2 API endpoint - this is where many packages publish their symbols
            $"https://www.nuget.org/api/v2/symbolpackage/{package.Id}/{package.Version}",
            $"https://globalcdn.nuget.org/packages/{package.Id.ToLowerInvariant()}.{package.Version.ToLowerInvariant()}.snupkg",
        };

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        foreach (var snupkgUrl in snupkgUrls)
        {
            try
            {
                var response = await httpClient.GetAsync(snupkgUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var fileStream = File.Create(snupkgPath);
                    await response.Content.CopyToAsync(fileStream, cancellationToken);
                    return snupkgPath;
                }
            }
            catch
            {
                // Try next URL
            }
        }

        return null;
    }

    public async Task<string> GetLatestVersionAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var versions = await GetAllVersionsAsync(packageId, cancellationToken);

        var stableVersions = versions
            .Select(v => NuGetVersion.Parse(v))
            .Where(v => !v.IsPrerelease)
            .OrderByDescending(v => v)
            .ToList();

        if (stableVersions.Any())
        {
            return stableVersions.First().ToNormalizedString();
        }

        // Fall back to latest prerelease
        var allVersions = versions
            .Select(v => NuGetVersion.Parse(v))
            .OrderByDescending(v => v)
            .ToList();

        if (allVersions.Any())
        {
            return allVersions.First().ToNormalizedString();
        }

        throw new NuGetPackageNotFoundException(packageId, null);
    }

    public async Task<List<string>> GetAllVersionsAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var cache = new SourceCacheContext();
        var repository = Repository.Factory.GetCoreV3(_sourceUrl);
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);

        var versions = await resource.GetAllVersionsAsync(
            packageId,
            cache,
            _logger,
            cancellationToken);

        return versions.Select(v => v.ToNormalizedString()).ToList();
    }
}
