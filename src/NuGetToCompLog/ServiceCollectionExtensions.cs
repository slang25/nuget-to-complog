using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Commands;
using NuGetToCompLog.Infrastructure.Console;
using NuGetToCompLog.Infrastructure.FileSystem;
using NuGetToCompLog.Infrastructure.Http;
using NuGetToCompLog.Infrastructure.SourceDownload;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.CompLog;
using NuGetToCompLog.Services.NuGet;
using NuGetToCompLog.Services.Patch;
using NuGetToCompLog.Services.Pdb;
using NuGetToCompLog.Services.References;

namespace NuGetToCompLog;

/// <summary>
/// Extension methods for registering services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NuGet to CompLog services.
    /// </summary>
    public static IServiceCollection AddNuGetToCompLogServices(
        this IServiceCollection services, 
        string? workingDirectory = null)
    {
        // HTTP clients (IHttpClientFactory) — one named client per remote, each with its own
        // pooled handler and config. Centralizes timeout/redirect/HTTP-version/header settings
        // that were previously duplicated across hand-rolled `new HttpClient()` call sites.
        services.AddHttpClient(HttpClientNames.SourceDownload, c =>
        {
            // SourceLink hosts are CDN-backed and multiplex over HTTP/2.
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestVersion = HttpVersion.Version20;
            c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            c.DefaultRequestHeaders.Add("User-Agent", "NuGetToCompLog/1.0");
        });
        services.AddHttpClient(HttpClientNames.SymbolPackage, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // The v2 symbolpackage endpoint 302-redirects to the symbol-packages CDN.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        });
        services.AddHttpClient(HttpClientNames.SymbolServer, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        });

        // Infrastructure services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IConsoleWriter, SpectreConsoleWriter>();
        services.AddSingleton<SourceFileDecompilerService>();
        services.AddSingleton<ISourceFileDownloader>(sp =>
        {
            var fileSystem = sp.GetRequiredService<IFileSystemService>();
            var console = sp.GetRequiredService<IConsoleWriter>();
            var decompiler = sp.GetRequiredService<SourceFileDecompilerService>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new HttpSourceFileDownloader(fileSystem, console, httpClientFactory, decompiler);
        });

        // NuGet services
        services.AddSingleton<INuGetClient, NuGetClientService>();
        services.AddSingleton<PackageExtractionService>();
        services.AddSingleton<ITargetFrameworkSelector, TargetFrameworkSelector>();

        // PDB services
        services.AddSingleton<PdbDiscoveryService>();
        services.AddSingleton<CompilationOptionsExtractor>();
        services.AddSingleton<IPdbReader, PdbReaderService>();
        services.AddSingleton<SourceLinkParser>();
        services.AddSingleton<SymbolServerClient>();

        // Reference resolution services
        // Note: ReferenceResolverService needs workingDirectory, so we register a factory
        if (workingDirectory != null)
        {
            services.AddSingleton<IReferenceResolver>(sp => 
                new ReferenceResolverService(workingDirectory));
        }
        else
        {
            // If no working directory provided, create one on demand
            services.AddSingleton<IReferenceResolver>(sp =>
            {
                var fileSystem = sp.GetRequiredService<IFileSystemService>();
                var tempDir = fileSystem.CreateTempDirectory();
                return new ReferenceResolverService(tempDir);
            });
        }

        // CompLog services
        services.AddSingleton<CompLogStructureCreator>();

        // Patch services
        services.AddSingleton<ProjectGenerator>();
        services.AddSingleton<PatchManager>();
        services.AddSingleton<UnifiedDiffGenerator>();
        services.AddSingleton<PatchApplier>();
        services.AddSingleton<AssemblyRebuilder>();

        // Pipeline and command handlers
        services.AddSingleton<PackageAnalysisPipeline>();
        services.AddSingleton<ProcessPackageCommandHandler>();
        services.AddSingleton<EjectPackageCommandHandler>();
        services.AddSingleton<DiffCommandHandler>();
        services.AddSingleton<ApplyCommandHandler>();

        return services;
    }
}
