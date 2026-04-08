using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.NuGet;
using NuGetToCompLog.Services.Pdb;
using NuGetToCompLog.Infrastructure.SourceDownload;

namespace NuGetToCompLog.Services;

/// <summary>
/// Reusable pipeline that downloads, extracts, and analyzes a NuGet package.
/// Produces a <see cref="PackageExtractionResult"/> that can be consumed by
/// different commands (complog creation, eject, etc.).
/// </summary>
public class PackageAnalysisPipeline
{
    private readonly INuGetClient _nugetClient;
    private readonly PackageExtractionService _extractionService;
    private readonly ITargetFrameworkSelector _tfmSelector;
    private readonly PdbDiscoveryService _pdbDiscovery;
    private readonly IPdbReader _pdbReader;
    private readonly ISourceFileDownloader _sourceDownloader;
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public PackageAnalysisPipeline(
        INuGetClient nugetClient,
        PackageExtractionService extractionService,
        ITargetFrameworkSelector tfmSelector,
        PdbDiscoveryService pdbDiscovery,
        IPdbReader pdbReader,
        ISourceFileDownloader sourceDownloader,
        IFileSystemService fileSystem,
        IConsoleWriter console)
    {
        _nugetClient = nugetClient;
        _extractionService = extractionService;
        _tfmSelector = tfmSelector;
        _pdbDiscovery = pdbDiscovery;
        _pdbReader = pdbReader;
        _sourceDownloader = sourceDownloader;
        _fileSystem = fileSystem;
        _console = console;
    }

    /// <summary>
    /// Analyzes a NuGet package: downloads, extracts, reads PDB metadata,
    /// downloads source files, and returns a result capturing all artifacts.
    /// </summary>
    public async Task<PackageExtractionResult?> AnalyzeAsync(
        string packageId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = _fileSystem.CreateTempDirectory();
        _console.MarkupLine($"[dim]Working directory: {workingDirectory}[/]");
        _console.WriteLine();

        if (version == null)
        {
            version = await _nugetClient.GetLatestVersionAsync(packageId, cancellationToken);
            _console.MarkupLine($"[dim]Latest version: {version}[/]");
        }

        var package = new PackageIdentity(packageId, version);

        // Download package
        string packagePath = "";
        await _console.ExecuteWithStatusAsync("Downloading package...", async () =>
        {
            packagePath = await _nugetClient.DownloadPackageAsync(package, workingDirectory, cancellationToken);
        });
        _console.MarkupLine($"[green]\u2713[/] Downloaded package to: [dim]{Path.GetFileName(packagePath)}[/]");
        _console.WriteLine();

        // Extract package
        var extractPath = Path.Combine(workingDirectory, "extracted");
        await _extractionService.ExtractPackageAsync(packagePath, extractPath);
        _console.MarkupLine("[green]\u2713[/] Extracted package");
        _console.WriteLine();

        // Find and select assemblies
        var allAssemblies = _extractionService.FindAssemblies(extractPath);
        DisplayAssembliesTree(allAssemblies, extractPath);

        var (selectedAssemblies, selectedTfm) = _tfmSelector.SelectBestTargetFramework(allAssemblies, extractPath);
        if (selectedTfm != null)
        {
            _console.MarkupLine($"[green]\u2713[/] Selected best TFM: [cyan]{selectedTfm}[/] with [yellow]{selectedAssemblies.Count}[/] assemblies");
            _console.WriteLine();
        }

        // Handle PDB discovery and symbols download
        var hasEmbeddedPdb = selectedAssemblies.Any(a => _pdbDiscovery.HasEmbeddedPdb(a));
        if (!hasEmbeddedPdb)
        {
            await TryDownloadSymbolsAsync(package, workingDirectory, cancellationToken);
        }
        else
        {
            _console.MarkupLine("[dim]\u26a0 Skipping symbols package download - selected assemblies have embedded PDBs[/]");
            _console.WriteLine();
        }

        // Process each assembly
        _console.MarkupLine($"  [cyan]\u2192[/] Processing {selectedAssemblies.Count} assembly/assemblies from TFM: [yellow]{selectedTfm}[/]");
        foreach (var assemblyPath in selectedAssemblies)
        {
            _console.MarkupLine($"     Assembly: [dim]{Path.GetFileName(assemblyPath)}[/]");
            await ProcessAssemblyAsync(assemblyPath, workingDirectory, cancellationToken);
            _console.WriteLine();
        }

        var compilerArgsFile = Path.Combine(workingDirectory, "compiler-arguments.txt");
        var metadataRefsFile = Path.Combine(workingDirectory, "metadata-references.txt");
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        var resourcesDir = Path.Combine(workingDirectory, "resources");

        return new PackageExtractionResult(
            Package: package,
            WorkingDirectory: workingDirectory,
            ExtractPath: extractPath,
            SelectedTfm: selectedTfm,
            SelectedAssemblies: selectedAssemblies,
            CompilerArgsFile: File.Exists(compilerArgsFile) ? compilerArgsFile : null,
            MetadataRefsFile: File.Exists(metadataRefsFile) ? metadataRefsFile : null,
            SourcesDirectory: sourcesDir,
            ResourcesDirectory: Directory.Exists(resourcesDir) ? resourcesDir : null);
    }

    private void DisplayAssembliesTree(List<string> assemblies, string extractPath)
    {
        var nodes = new Dictionary<string, List<string>>();

        foreach (var assembly in assemblies)
        {
            var relativePath = Path.GetRelativePath(extractPath, assembly);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);
            var framework = parts.Length > 1 ? parts[1] : "unknown";

            if (!nodes.ContainsKey($"[cyan]{framework}[/]"))
            {
                nodes[$"[cyan]{framework}[/]"] = new List<string>();
            }
            nodes[$"[cyan]{framework}[/]"].Add($"[yellow]{Path.GetFileName(assembly)}[/]");
        }

        _console.WriteTree($"[green]Found {assemblies.Count} assemblies across all TFMs[/]", nodes);
        _console.WriteLine();
    }

    private async Task TryDownloadSymbolsAsync(PackageIdentity package, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            string? snupkgPath = null;
            await _console.ExecuteWithStatusAsync("Attempting to download symbols package (.snupkg)...", async () =>
            {
                snupkgPath = await _nugetClient.DownloadSymbolsPackageAsync(package, workingDirectory, cancellationToken);
            });

            if (snupkgPath != null)
            {
                var symbolsExtractPath = Path.Combine(workingDirectory, "symbols");
                await _extractionService.ExtractPackageAsync(snupkgPath, symbolsExtractPath);

                var pdbs = _extractionService.FindPdbFiles(symbolsExtractPath);
                if (pdbs.Count > 0)
                {
                    var nodes = new Dictionary<string, List<string>>();
                    nodes["PDB Files"] = pdbs.Select(p => $"[blue]{Path.GetRelativePath(symbolsExtractPath, p)}[/]").ToList();
                    _console.WriteTree($"[green]\u2713 Downloaded symbols package with {pdbs.Count} PDB file(s)[/]", nodes);
                }
                else
                {
                    _console.MarkupLine("[green]\u2713[/] Downloaded symbols package (no PDB files found inside)");
                }
                _console.WriteLine();
            }
            else
            {
                _console.MarkupLine("[yellow]\u26a0[/] Symbols package (.snupkg) not available for this package");
                _console.MarkupLine("   [dim]Note: Not all packages publish symbol packages to NuGet.org[/]");
                _console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]\u26a0[/] Could not download symbols package: [dim]{ex.Message}[/]");
            _console.WriteLine();
        }
    }

    private async Task ProcessAssemblyAsync(string assemblyPath, string workingDirectory, CancellationToken cancellationToken)
    {
        var hasEmbeddedPdb = _pdbDiscovery.HasEmbeddedPdb(assemblyPath);
        var hasReproducibleMarker = _pdbDiscovery.HasReproducibleMarker(assemblyPath);

        if (hasEmbeddedPdb)
        {
            _console.MarkupLine("  [green]\u2713 Found embedded PDB[/]");
            if (hasReproducibleMarker)
            {
                _console.MarkupLine("  [green]\u2713 Found reproducible/deterministic marker[/]");
            }

            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, null, hasReproducibleMarker, cancellationToken);
            using var pdbHandle = GetPdbMetadataReader(assemblyPath, null);
            await SaveMetadataAsync(metadata, assemblyPath, pdbHandle?.Reader, workingDirectory);
            return;
        }

        var pdbPath = await _pdbReader.FindPdbAsync(assemblyPath, workingDirectory);
        if (pdbPath != null)
        {
            _console.MarkupLine($"  [green]\u2713 Found external PDB:[/] [cyan]{Path.GetFileName(pdbPath)}[/]");
            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, pdbPath, hasReproducibleMarker, cancellationToken);
            using var pdbHandle = GetPdbMetadataReader(assemblyPath, pdbPath);
            await SaveMetadataAsync(metadata, assemblyPath, pdbHandle?.Reader, workingDirectory);
        }
        else
        {
            _console.WritePanel(
                "\u26a0 Missing Symbols",
                "[yellow]No PDB found[/] - cannot extract compiler arguments\n\n" +
                "[dim]Note: Reproducible builds with embedded symbols are required for complog extraction[/]",
                "Yellow");
        }
    }

    private sealed class PdbReaderHandle : IDisposable
    {
        private readonly System.Reflection.Metadata.MetadataReaderProvider _provider;
        public System.Reflection.Metadata.MetadataReader Reader { get; }

        public PdbReaderHandle(System.Reflection.Metadata.MetadataReaderProvider provider)
        {
            _provider = provider;
            Reader = provider.GetMetadataReader();
        }

        public void Dispose() => _provider.Dispose();
    }

    private PdbReaderHandle? GetPdbMetadataReader(string assemblyPath, string? pdbPath)
    {
        try
        {
            if (pdbPath == null)
            {
                byte[] pdbBytes;

                using (var peStream = File.OpenRead(assemblyPath))
                using (var peReader = new System.Reflection.PortableExecutable.PEReader(peStream))
                {
                    var embeddedPdb = peReader.ReadDebugDirectory()
                        .FirstOrDefault(d => d.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.EmbeddedPortablePdb);
                    if (embeddedPdb.Type != System.Reflection.PortableExecutable.DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        return null;
                    }

                    var tempProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
                    var pdbSize = embeddedPdb.DataSize;
                    pdbBytes = new byte[pdbSize];
                    var section = peReader.GetSectionData(embeddedPdb.DataRelativeVirtualAddress);
                    var span = section.GetContent(0, pdbSize);
                    span.CopyTo(pdbBytes);
                    tempProvider.Dispose();
                }

                var immutableBytes = System.Collections.Immutable.ImmutableArray.Create(pdbBytes);
                var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(immutableBytes);
                return new PdbReaderHandle(provider);
            }
            else
            {
                var pdbBytes = File.ReadAllBytes(pdbPath);
                var immutableBytes = System.Collections.Immutable.ImmutableArray.Create(pdbBytes);
                var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(immutableBytes);
                return new PdbReaderHandle(provider);
            }
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveMetadataAsync(
        PdbMetadata metadata,
        string assemblyPath,
        System.Reflection.Metadata.MetadataReader? pdbMetadataReader,
        string workingDirectory)
    {
        if (metadata.CompilerArguments.Count > 0)
        {
            var compilerArgsPath = Path.Combine(workingDirectory, "compiler-arguments.txt");
            await _fileSystem.WriteAllLinesAsync(compilerArgsPath, metadata.CompilerArguments);
            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.CompilerArguments.Count} compiler arguments");
        }

        if (metadata.MetadataReferences.Count > 0)
        {
            var referencesPath = Path.Combine(workingDirectory, "metadata-references.txt");
            await _fileSystem.WriteAllLinesAsync(referencesPath, metadata.MetadataReferences.Select(r => r.FileName));
            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.MetadataReferences.Count} metadata references");
        }

        if (metadata.EmbeddedResources.Count > 0)
        {
            var resourcesDir = Path.Combine(workingDirectory, "resources");
            _fileSystem.CreateDirectory(resourcesDir);

            var resourceMappings = new List<string>();

            foreach (var resource in metadata.EmbeddedResources)
            {
                var fileName = resource.Name.Replace("/", "_").Replace("\\", "_");
                var filePath = Path.Combine(resourcesDir, fileName);
                await _fileSystem.WriteAllBytesAsync(filePath, resource.Content);

                resourceMappings.Add($"{fileName}|{resource.Name}");
            }

            var mappingPath = Path.Combine(workingDirectory, "resource-mappings.txt");
            await _fileSystem.WriteAllLinesAsync(mappingPath, resourceMappings);

            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.EmbeddedResources.Count} embedded resource(s)");
        }

        if (metadata.SourceFiles.Count > 0)
        {
            var sourcesDir = Path.Combine(workingDirectory, "sources");
            _fileSystem.CreateDirectory(sourcesDir);

            var embeddedCount = 0;
            foreach (var sourceFile in metadata.SourceFiles.Where(sf => sf.HasContent))
            {
                var fileName = Path.GetFileName(sourceFile.Path);
                var filePath = Path.Combine(sourcesDir, fileName);
                await _fileSystem.WriteAllTextAsync(filePath, sourceFile.Content!);
                embeddedCount++;
            }

            if (embeddedCount > 0)
            {
                _console.MarkupLine($"  [green]\u2713[/] Saved {embeddedCount} embedded source files");
            }

            if (!string.IsNullOrEmpty(metadata.SourceLinkJson))
            {
                if (_sourceDownloader is HttpSourceFileDownloader httpDownloader)
                {
                    var downloadedCount = await httpDownloader.DownloadSourceFilesAsync(
                        metadata.SourceFiles,
                        metadata.SourceLinkJson,
                        sourcesDir,
                        assemblyPath,
                        pdbMetadataReader);

                    if (downloadedCount > 0)
                    {
                        _console.MarkupLine($"  [green]\u2713[/] Downloaded {downloadedCount} source files from Source Link");
                    }
                }
                else
                {
                    var downloadedCount = await _sourceDownloader.DownloadSourceFilesAsync(
                        metadata.SourceFiles,
                        metadata.SourceLinkJson,
                        sourcesDir);

                    if (downloadedCount > 0)
                    {
                        _console.MarkupLine($"  [green]\u2713[/] Downloaded {downloadedCount} source files from Source Link");
                    }
                }
            }
        }
    }
}
