using System.Text.Json;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.CompLog;

/// <summary>
/// Creates a structured output directory with all compilation artifacts.
/// </summary>
public class CompLogStructureCreator
{
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public CompLogStructureCreator(IFileSystemService fileSystem, IConsoleWriter console)
    {
        _fileSystem = fileSystem;
        _console = console;
    }

    /// <summary>
    /// Creates a structured output directory with all compilation artifacts.
    /// </summary>
    public string CreateStructure(
        string packageId,
        string version,
        string workingDirectory,
        string? outputDirectory = null,
        List<string>? selectedAssemblies = null)
    {
        var outputPath = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}-{version}-complog");
        _fileSystem.CreateDirectory(outputPath);

        _console.WriteLine();
        _console.MarkupLine("[yellow]Creating CompLog structure...[/]");

        // Copy sources
        CopySources(workingDirectory, outputPath);

        // Copy resources
        CopyResources(workingDirectory, outputPath);

        // Copy references
        CopyReferences(workingDirectory, outputPath, selectedAssemblies);

        // Copy compiler artifacts
        CopyCompilerArtifacts(workingDirectory, outputPath);

        // Copy symbols
        CopySymbols(workingDirectory, outputPath);

        // Create metadata
        CreateMetadata(packageId, version, outputPath);

        DisplaySummary(outputPath);

        return outputPath;
    }

    private void CopySources(string workingDirectory, string outputPath)
    {
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        if (_fileSystem.DirectoryExists(sourcesDir))
        {
            var targetSourcesDir = Path.Combine(outputPath, "sources");
            CopyDirectory(sourcesDir, targetSourcesDir);
            var fileCount = _fileSystem.GetFiles(targetSourcesDir, "*.*", SearchOption.AllDirectories).Length;
            _console.MarkupLine($"  [green]✓[/] Copied {fileCount} source files");
        }
    }

    private void CopyResources(string workingDirectory, string outputPath)
    {
        var resourcesDir = Path.Combine(workingDirectory, "resources");
        if (_fileSystem.DirectoryExists(resourcesDir))
        {
            var targetResourcesDir = Path.Combine(outputPath, "resources");
            CopyDirectory(resourcesDir, targetResourcesDir);
            var fileCount = _fileSystem.GetFiles(targetResourcesDir, "*.*", SearchOption.AllDirectories).Length;
            _console.MarkupLine($"  [green]✓[/] Copied {fileCount} embedded resource file(s)");
        }
    }

    private void CopyReferences(string workingDirectory, string outputPath, List<string>? selectedAssemblies)
    {
        var extractedDir = Path.Combine(workingDirectory, "extracted");
        if (!_fileSystem.DirectoryExists(extractedDir))
        {
            return;
        }

        var targetRefsDir = Path.Combine(outputPath, "references");
        _fileSystem.CreateDirectory(targetRefsDir);

        int refCount = 0;

        if (selectedAssemblies != null && selectedAssemblies.Count > 0)
        {
            // Copy only selected assemblies
            foreach (var dll in selectedAssemblies)
            {
                if (_fileSystem.FileExists(dll))
                {
                    var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                    _fileSystem.CopyFile(dll, dest, true);
                    refCount++;
                }
            }
        }
        else
        {
            // Copy all DLLs from lib and ref folders
            var libDir = Path.Combine(extractedDir, "lib");
            var refDir = Path.Combine(extractedDir, "ref");

            if (_fileSystem.DirectoryExists(libDir))
            {
                foreach (var dll in _fileSystem.GetFiles(libDir, "*.dll", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                    _fileSystem.CopyFile(dll, dest, true);
                    refCount++;
                }
            }

            if (_fileSystem.DirectoryExists(refDir))
            {
                foreach (var dll in _fileSystem.GetFiles(refDir, "*.dll", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                    if (!_fileSystem.FileExists(dest))
                    {
                        _fileSystem.CopyFile(dll, dest, true);
                        refCount++;
                    }
                }
            }
        }

        if (refCount > 0)
        {
            _console.MarkupLine($"  [green]✓[/] Copied {refCount} reference assemblies");
        }
    }

    private void CopyCompilerArtifacts(string workingDirectory, string outputPath)
    {
        var compilerArgsFile = Path.Combine(workingDirectory, "compiler-arguments.txt");
        if (_fileSystem.FileExists(compilerArgsFile))
        {
            _fileSystem.CopyFile(compilerArgsFile, Path.Combine(outputPath, "compiler-arguments.txt"), true);
            _console.MarkupLine("  [green]✓[/] Copied compiler arguments");
        }

        var metadataRefsFile = Path.Combine(workingDirectory, "metadata-references.txt");
        if (_fileSystem.FileExists(metadataRefsFile))
        {
            _fileSystem.CopyFile(metadataRefsFile, Path.Combine(outputPath, "metadata-references.txt"), true);
            _console.MarkupLine("  [green]✓[/] Copied metadata references");
        }

        var resourceMappingsFile = Path.Combine(workingDirectory, "resource-mappings.txt");
        if (_fileSystem.FileExists(resourceMappingsFile))
        {
            _fileSystem.CopyFile(resourceMappingsFile, Path.Combine(outputPath, "resource-mappings.txt"), true);
            _console.MarkupLine("  [green]✓[/] Copied resource mappings");
        }
    }

    private void CopySymbols(string workingDirectory, string outputPath)
    {
        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (_fileSystem.DirectoryExists(symbolsDir))
        {
            var targetSymbolsDir = Path.Combine(outputPath, "symbols");
            CopyDirectory(symbolsDir, targetSymbolsDir);
            var pdbCount = _fileSystem.GetFiles(targetSymbolsDir, "*.pdb", SearchOption.AllDirectories).Length;
            if (pdbCount > 0)
            {
                _console.MarkupLine($"  [green]✓[/] Copied {pdbCount} symbol files");
            }
        }
    }

    private void CreateMetadata(string packageId, string version, string outputPath)
    {
        var sourcesPath = Path.Combine(outputPath, "sources");
        var sourceCount = _fileSystem.DirectoryExists(sourcesPath)
            ? _fileSystem.GetFiles(sourcesPath, "*.*", SearchOption.AllDirectories).Length
            : 0;

        var metadata = new
        {
            PackageId = packageId,
            Version = version,
            CreatedAt = DateTime.UtcNow,
            SourceCount = sourceCount,
            Note = "This directory contains all artifacts extracted from the NuGet package."
        };

        var metadataPath = Path.Combine(outputPath, "metadata.json");
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        _fileSystem.WriteAllTextAsync(metadataPath, json).Wait();
        _console.MarkupLine("  [green]✓[/] Created metadata file");
    }

    private void DisplaySummary(string outputPath)
    {
        _console.WriteLine();
        _console.WritePanel(
            "CompLog Structure Created",
            $"[green]CompLog structure created successfully![/]\n\n" +
            $"[cyan]Output directory:[/] {outputPath}\n\n" +
            $"[yellow]Contents:[/]\n" +
            $"  • sources/ - Extracted source files\n" +
            $"  • resources/ - Embedded resource files\n" +
            $"  • references/ - Assembly references\n" +
            $"  • symbols/ - PDB symbol files (if available)\n" +
            $"  • compiler-arguments.txt - Compiler command-line arguments\n" +
            $"  • metadata-references.txt - List of referenced assemblies\n" +
            $"  • metadata.json - Package metadata\n\n" +
            $"[dim]This directory contains all artifacts extracted from the NuGet package.[/]",
            "Green");
    }

    private void CopyDirectory(string sourceDir, string targetDir)
    {
        _fileSystem.CreateDirectory(targetDir);

        foreach (var file in _fileSystem.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            _fileSystem.CopyFile(file, targetFile, true);
        }

        foreach (var subDir in _fileSystem.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }
}
