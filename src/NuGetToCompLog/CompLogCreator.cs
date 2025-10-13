using Spectre.Console;
using System.Text.Json;

namespace NuGetToCompLog;

/// <summary>
/// Creates a structured output directory that contains all the information needed
/// for creating a CompLog file. This includes compiler arguments, source files,
/// references, and metadata extracted from the NuGet package.
/// </summary>
public class CompLogCreator
{
    /// <summary>
    /// Creates a structured output directory with all compilation artifacts.
    /// Returns the path to the created directory.
    /// </summary>
    public static string CreateCompLogStructure(
        string packageId,
        string version,
        string workingDirectory,
        string? outputDirectory = null,
        List<string>? selectedAssemblies = null) // Optional: only copy these specific assemblies
    {
        // Create output directory
        var outputPath = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), $"{packageId}-{version}-complog");
        Directory.CreateDirectory(outputPath);

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[yellow]Creating CompLog structure...[/]");

        // Copy sources
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        if (Directory.Exists(sourcesDir))
        {
            var targetSourcesDir = Path.Combine(outputPath, "sources");
            CopyDirectory(sourcesDir, targetSourcesDir);
            var fileCount = Directory.GetFiles(targetSourcesDir, "*.*", SearchOption.AllDirectories).Length;
            AnsiConsole.MarkupLine($"  [green]✓[/] Copied {fileCount} source files");
        }

        // Copy references (assemblies)
        var extractedDir = Path.Combine(workingDirectory, "extracted");
        if (Directory.Exists(extractedDir))
        {
            var targetRefsDir = Path.Combine(outputPath, "references");
            Directory.CreateDirectory(targetRefsDir);
            
            int refCount = 0;
            
            // If specific assemblies were selected, only copy those
            if (selectedAssemblies != null && selectedAssemblies.Count > 0)
            {
                foreach (var dll in selectedAssemblies)
                {
                    if (File.Exists(dll))
                    {
                        var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                        File.Copy(dll, dest, true);
                        refCount++;
                    }
                }
            }
            else
            {
                // Otherwise, copy all DLLs from lib and ref folders
                var libDir = Path.Combine(extractedDir, "lib");
                var refDir = Path.Combine(extractedDir, "ref");
                
                if (Directory.Exists(libDir))
                {
                    foreach (var dll in Directory.GetFiles(libDir, "*.dll", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                        File.Copy(dll, dest, true);
                        refCount++;
                    }
                }
                if (Directory.Exists(refDir))
                {
                    foreach (var dll in Directory.GetFiles(refDir, "*.dll", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(targetRefsDir, Path.GetFileName(dll));
                        if (!File.Exists(dest))
                        {
                            File.Copy(dll, dest, true);
                            refCount++;
                        }
                    }
                }
            }
            
            if (refCount > 0)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] Copied {refCount} reference assemblies");
            }
        }

        // Copy compiler arguments and metadata references if available
        var compilerArgsFile = Path.Combine(workingDirectory, "compiler-arguments.txt");
        if (File.Exists(compilerArgsFile))
        {
            File.Copy(compilerArgsFile, Path.Combine(outputPath, "compiler-arguments.txt"), true);
            AnsiConsole.MarkupLine($"  [green]✓[/] Copied compiler arguments");
        }
        
        var metadataRefsFile = Path.Combine(workingDirectory, "metadata-references.txt");
        if (File.Exists(metadataRefsFile))
        {
            File.Copy(metadataRefsFile, Path.Combine(outputPath, "metadata-references.txt"), true);
            AnsiConsole.MarkupLine($"  [green]✓[/] Copied metadata references");
        }

        // Copy symbols if available
        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (Directory.Exists(symbolsDir))
        {
            var targetSymbolsDir = Path.Combine(outputPath, "symbols");
            CopyDirectory(symbolsDir, targetSymbolsDir);
            var pdbCount = Directory.GetFiles(targetSymbolsDir, "*.pdb", SearchOption.AllDirectories).Length;
            if (pdbCount > 0)
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] Copied {pdbCount} symbol files");
            }
        }

        // Create a metadata file
        var metadata = new
        {
            PackageId = packageId,
            Version = version,
            CreatedAt = DateTime.UtcNow,
            SourceCount = Directory.Exists(Path.Combine(outputPath, "sources")) 
                ? Directory.GetFiles(Path.Combine(outputPath, "sources"), "*.*", SearchOption.AllDirectories).Length 
                : 0,
            Note = "This directory contains all artifacts extracted from the NuGet package."
        };

        var metadataPath = Path.Combine(outputPath, "metadata.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        AnsiConsole.MarkupLine($"  [green]✓[/] Created metadata file");

        AnsiConsole.MarkupLine("");
        var panel = new Panel(
            $"[green]CompLog structure created successfully![/]\n\n" +
            $"[cyan]Output directory:[/] {outputPath}\n\n" +
            $"[yellow]Contents:[/]\n" +
            $"  • sources/ - Extracted source files\n" +
            $"  • references/ - Assembly references\n" +
            $"  • symbols/ - PDB symbol files (if available)\n" +
            $"  • compiler-arguments.txt - Compiler command-line arguments\n" +
            $"  • metadata-references.txt - List of referenced assemblies\n" +
            $"  • metadata.json - Package metadata\n\n" +
            $"[dim]This directory contains all artifacts extracted from the NuGet package.[/]")
            .Header("[green]CompLog Structure Created[/]")
            .BorderColor(Color.Green)
            .Expand();
        
        AnsiConsole.Write(panel);

        return outputPath;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }
}
