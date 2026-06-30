namespace NuGetToCompLog.Infrastructure.Http;

/// <summary>
/// Names for the <see cref="System.Net.Http.IHttpClientFactory"/> clients registered in
/// <see cref="ServiceCollectionExtensions"/>. Each name maps to a client with its own
/// pooled handler and centralized configuration (timeout, redirects, HTTP version, headers).
/// </summary>
public static class HttpClientNames
{
    /// <summary>Source files fetched from SourceLink hosts (raw.githubusercontent.com and friends).</summary>
    public const string SourceDownload = "source-download";

    /// <summary>Symbol packages (.snupkg) fetched from the nuget.org symbol-packages CDN.</summary>
    public const string SymbolPackage = "symbol-package";

    /// <summary>Individual portable PDBs fetched from public symbol servers via SSQP.</summary>
    public const string SymbolServer = "symbol-server";
}
