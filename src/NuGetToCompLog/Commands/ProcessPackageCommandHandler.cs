using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.NuGet;
using NuGetToCompLog.Services.Pdb;
using NuGetToCompLog.Services.References;
using NuGetToCompLog.Services.CompLog;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the ProcessPackageCommand by orchestrating all the services.
/// </summary>
public class ProcessPackageCommandHandler
{
    private readonly INuGetClient _nugetClient;
    private readonly PackageExtractionService _extractionService;
    private readonly ITargetFrameworkSelector _tfmSelector;
    private readonly PdbDiscoveryService _pdbDiscovery;
    private readonly IPdbReader _pdbReader;
    private readonly IReferenceResolver _referenceResolver;
    private readonly CompLogStructureCreator _structureCreator;
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public ProcessPackageCommandHandler(
        INuGetClient nugetClient,
        PackageExtractionService extractionService,
        ITargetFrameworkSelector tfmSelector,
        PdbDiscoveryService pdbDiscovery,
        IPdbReader pdbReader,
        IReferenceResolver referenceResolver,
        CompLogStructureCreator structureCreator,
        IFileSystemService fileSystem,
        IConsoleWriter console)
    {
        _nugetClient = nugetClient;
        _extractionService = extractionService;
        _tfmSelector = tfmSelector;
        _pdbDiscovery = pdbDiscovery;
        _pdbReader = pdbReader;
        _referenceResolver = referenceResolver;
        _structureCreator = structureCreator;
        _fileSystem = fileSystem;
        _console = console;
    }

    public async Task<string?> HandleAsync(ProcessPackageCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            // Display header
            DisplayHeader(command.PackageId, command.Version);

            // Create working directory
            var workingDirectory = _fileSystem.CreateTempDirectory();
            _console.MarkupLine($"[dim]Working directory: {workingDirectory}[/]");
            _console.WriteLine();

            // Step 1: Resolve version if needed
            var version = command.Version;
            if (version == null)
            {
                version = await _nugetClient.GetLatestVersionAsync(command.PackageId, cancellationToken);
                _console.MarkupLine($"[dim]Latest version: {version}[/]");
            }

            var package = new PackageIdentity(command.PackageId, version);

            // Step 2: Download package
            string packagePath = "";
            await _console.ExecuteWithStatusAsync("Downloading package...", async () =>
            {
                packagePath = await _nugetClient.DownloadPackageAsync(package, workingDirectory, cancellationToken);
            });
            _console.MarkupLine($"[green]✓[/] Downloaded package to: [dim]{Path.GetFileName(packagePath)}[/]");
            _console.WriteLine();

            // Step 3: Extract package
            var extractPath = Path.Combine(workingDirectory, "extracted");
            await _extractionService.ExtractPackageAsync(packagePath, extractPath);
            _console.MarkupLine("[green]✓[/] Extracted package");
            _console.WriteLine();

            // Step 4: Find assemblies
            var allAssemblies = _extractionService.FindAssemblies(extractPath);
            DisplayAssembliesTree(allAssemblies, extractPath);

            // Step 5: Select best TFM
            var (selectedAssemblies, selectedTfm) = _tfmSelector.SelectBestTargetFramework(allAssemblies, extractPath);
            if (selectedTfm != null)
            {
                _console.MarkupLine($"[green]✓[/] Selected best TFM: [cyan]{selectedTfm}[/] with [yellow]{selectedAssemblies.Count}[/] assemblies");
                _console.WriteLine();
            }

            // Step 6: Try to download symbols
            await TryDownloadSymbolsAsync(package, workingDirectory, cancellationToken);

            // Step 7: Process assemblies to extract compiler arguments
            _console.MarkupLine($"  [cyan]→[/] Processing {selectedAssemblies.Count} assembly/assemblies from TFM: [yellow]{selectedTfm}[/]");
            
            foreach (var assemblyPath in selectedAssemblies)
            {
                _console.MarkupLine($"     Assembly: [dim]{Path.GetFileName(assemblyPath)}[/]");
                await ProcessAssemblyAsync(assemblyPath, workingDirectory, cancellationToken);
                _console.WriteLine();
            }

            // Step 8: Create CompLog structure
            var complogStructurePath = _structureCreator.CreateStructure(
                command.PackageId,
                version,
                workingDirectory,
                null,
                selectedAssemblies);

            // Step 9: Create actual .complog file (using existing CompLogFileCreator for now)
            var complogFilePath = await CompLogFileCreator.CreateCompLogFileAsync(
                command.PackageId,
                version,
                workingDirectory,
                Directory.GetCurrentDirectory(),
                selectedTfm);

            return complogFilePath;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return null;
        }
    }

    private void DisplayHeader(string packageId, string? version)
    {
        _console.WritePanel(
            "Processing Package",
            $"[cyan]{packageId}[/] {(version != null ? $"[yellow]{version}[/]" : "[dim](latest)[/]")}",
            "Cyan1");
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
                    _console.WriteTree($"[green]✓ Downloaded symbols package with {pdbs.Count} PDB file(s)[/]", nodes);
                }
                else
                {
                    _console.MarkupLine("[green]✓[/] Downloaded symbols package (no PDB files found inside)");
                }
                _console.WriteLine();
            }
            else
            {
                _console.MarkupLine("[yellow]⚠[/] Symbols package (.snupkg) not available for this package");
                _console.MarkupLine("   [dim]Note: Not all packages publish symbol packages to NuGet.org[/]");
                _console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]⚠[/] Could not download symbols package: [dim]{ex.Message}[/]");
            _console.WriteLine();
        }
    }

    private async Task ProcessAssemblyAsync(string assemblyPath, string workingDirectory, CancellationToken cancellationToken)
    {
        // Check for embedded PDB
        var hasEmbeddedPdb = _pdbDiscovery.HasEmbeddedPdb(assemblyPath);
        var hasReproducibleMarker = _pdbDiscovery.HasReproducibleMarker(assemblyPath);

        if (hasEmbeddedPdb)
        {
            _console.MarkupLine("  [green]✓ Found embedded PDB[/]");
            if (hasReproducibleMarker)
            {
                _console.MarkupLine("  [green]✓ Found reproducible/deterministic marker[/]");
            }

            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, null, hasReproducibleMarker, cancellationToken);
            await SaveMetadataAsync(metadata, workingDirectory);
            return;
        }

        // Look for external PDB
        var pdbPath = await _pdbReader.FindPdbAsync(assemblyPath, workingDirectory);
        if (pdbPath != null)
        {
            _console.MarkupLine($"  [green]✓ Found external PDB:[/] [cyan]{Path.GetFileName(pdbPath)}[/]");
            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, pdbPath, hasReproducibleMarker, cancellationToken);
            await SaveMetadataAsync(metadata, workingDirectory);
        }
        else
        {
            _console.WritePanel(
                "⚠ Missing Symbols",
                "[yellow]No PDB found[/] - cannot extract compiler arguments\n\n" +
                "[dim]Note: Reproducible builds with embedded symbols are required for complog extraction[/]",
                "Yellow");
        }
    }

    private async Task SaveMetadataAsync(PdbMetadata metadata, string workingDirectory)
    {
        // Save compiler arguments
        if (metadata.CompilerArguments.Count > 0)
        {
            var compilerArgsPath = Path.Combine(workingDirectory, "compiler-arguments.txt");
            await _fileSystem.WriteAllLinesAsync(compilerArgsPath, metadata.CompilerArguments);
            _console.MarkupLine($"  [green]✓[/] Saved {metadata.CompilerArguments.Count} compiler arguments");
        }

        // Save metadata references
        if (metadata.MetadataReferences.Count > 0)
        {
            var referencesPath = Path.Combine(workingDirectory, "metadata-references.txt");
            await _fileSystem.WriteAllLinesAsync(referencesPath, metadata.MetadataReferences.Select(r => r.FileName));
            _console.MarkupLine($"  [green]✓[/] Saved {metadata.MetadataReferences.Count} metadata references");
        }

        // Save source files
        if (metadata.SourceFiles.Count > 0)
        {
            var sourcesDir = Path.Combine(workingDirectory, "sources");
            _fileSystem.CreateDirectory(sourcesDir);
            
            var savedCount = 0;
            foreach (var sourceFile in metadata.SourceFiles.Where(sf => sf.HasContent))
            {
                var fileName = Path.GetFileName(sourceFile.Path);
                var filePath = Path.Combine(sourcesDir, fileName);
                await _fileSystem.WriteAllTextAsync(filePath, sourceFile.Content!);
                savedCount++;
            }

            if (savedCount > 0)
            {
                _console.MarkupLine($"  [green]✓[/] Saved {savedCount} embedded source files");
            }
        }
    }
}
