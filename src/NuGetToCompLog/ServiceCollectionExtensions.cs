using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Commands;
using NuGetToCompLog.Infrastructure.Console;
using NuGetToCompLog.Infrastructure.FileSystem;
using NuGetToCompLog.Infrastructure.SourceDownload;
using NuGetToCompLog.Services.CompLog;
using NuGetToCompLog.Services.NuGet;
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
        // Infrastructure services
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IConsoleWriter, SpectreConsoleWriter>();
        services.AddSingleton<SourceFileDecompilerService>();
        services.AddSingleton<ISourceFileDownloader>(sp =>
        {
            var fileSystem = sp.GetRequiredService<IFileSystemService>();
            var console = sp.GetRequiredService<IConsoleWriter>();
            var decompiler = sp.GetRequiredService<SourceFileDecompilerService>();
            return new HttpSourceFileDownloader(fileSystem, console, decompiler);
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

        // Command handler
        services.AddSingleton<ProcessPackageCommandHandler>();

        return services;
    }
}
