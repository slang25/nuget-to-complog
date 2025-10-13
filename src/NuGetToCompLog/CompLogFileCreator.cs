using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Spectre.Console;

namespace NuGetToCompLog;

/// <summary>
/// Creates a .complog file from extracted compiler arguments and sources.
/// Uses the internal CompilerLogBuilder API (accessible via IgnoresAccessChecksTo).
/// </summary>
public class CompLogFileCreator
{
    public static async Task<string> CreateCompLogFileAsync(
        string packageId,
        string version,
        string workingDirectory,
        string outputDirectory,
        string? overrideTfm = null) // Allow overriding the TFM from PDB
    {
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[yellow]Creating .complog file...[/]");

        var complogPath = Path.Combine(outputDirectory, $"{packageId}.{version}.complog");
        
        // Load compiler arguments and metadata references
        var compilerArgsFile = Path.Combine(workingDirectory, "compiler-arguments.txt");
        var metadataRefsFile = Path.Combine(workingDirectory, "metadata-references.txt");
        
        if (!File.Exists(compilerArgsFile))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] No compiler arguments found - cannot create complog");
            return complogPath;
        }

        var compilerArgs = await File.ReadAllLinesAsync(compilerArgsFile);
        var diagnostics = new List<string>();

        try
        {
            await using var complogStream = new FileStream(complogPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            
            // Now we can directly instantiate the internal CompilerLogBuilder
            using var builder = new CompilerLogBuilder(complogStream, diagnostics);

            // Find the assembly with the best TFM
            var extractedDir = Path.Combine(workingDirectory, "extracted");
            var assemblies = FindAssemblies(extractedDir);
            
            if (assemblies.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] No assemblies found in package");
                return complogPath;
            }

            // Get the first assembly (we already selected best TFM)
            var assemblyPath = assemblies[0];
            var isCSharp = !assemblyPath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

            // Determine compiler path
            var compilerPath = FindCompilerPath(isCSharp);
            
            // Parse the compiler arguments dictionary
            var argsDict = ParseCompilerArgumentsFile(compilerArgs);
            
            // Extract target framework - use override if provided, otherwise extract from args
            var targetFramework = overrideTfm ?? ExtractTargetFramework(argsDict);
            
            // Log which TFM we're using
            if (overrideTfm != null && overrideTfm != ExtractTargetFramework(argsDict))
            {
                AnsiConsole.MarkupLine($"  [cyan]→[/] Overriding TFM: [yellow]{ExtractTargetFramework(argsDict)}[/] → [green]{overrideTfm}[/]");
                AnsiConsole.MarkupLine($"     [dim](Using selected package TFM instead of PDB TFM)[/]");
            }
            
            // Load metadata references
            var metadataReferences = new List<MetadataReference>();
            if (File.Exists(metadataRefsFile))
            {
                var refLines = await File.ReadAllLinesAsync(metadataRefsFile);
                metadataReferences = refLines
                    .Select(line => new MetadataReference(
                        FileName: line,
                        ExternAliases: [],
                        EmbedInteropTypes: false,
                        Kind: MetadataImageKind.Assembly,
                        Timestamp: 0,
                        ImageSize: 0,
                        Mvid: Guid.Empty))
                    .ToList();
            }

            // Acquire all reference assemblies
            Dictionary<string, string> acquiredReferences = new();
            if (metadataReferences.Count > 0 && !string.IsNullOrEmpty(targetFramework))
            {
                var acquisitionService = new ReferenceAssemblyAcquisitionService(workingDirectory);
                acquiredReferences = await acquisitionService.AcquireAllReferencesAsync(metadataReferences, targetFramework);
                
                // If we didn't acquire any references, compilation will fail
                if (acquiredReferences.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/] No reference assemblies acquired - complog may not be complete");
                    diagnostics.Add($"Warning: No reference assemblies were acquired for {targetFramework}");
                }
            }
            else
            {
                if (metadataReferences.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/] No metadata references found in PDB");
                }
            }

            // Build compiler arguments string array, including reference paths
            var args = BuildCompilerArguments(argsDict, assemblyPath, workingDirectory, acquiredReferences);

            // Prepare paths for CommandLineParser
            // Source files are now saved directly in sources/ without extra nesting
            // so the project directory should be sources/
            var projectDir = Path.Combine(workingDirectory, "sources");
            var projectFilePath = Path.Combine(projectDir, $"{packageId}.csproj");
            
            var compilerCall = new CompilerCall(
                projectFilePath: projectFilePath,
                compilerFilePath: compilerPath,
                kind: CompilerCallKind.Regular,
                targetFramework: targetFramework,
                isCSharp: isCSharp,
                arguments: args);

            // Parse the command line arguments
            // Use projectDir as base so source file paths resolve correctly
            CommandLineArguments commandLineArguments;
            
            if (isCSharp)
            {
                commandLineArguments = CSharpCommandLineParser.Default.Parse(
                    args,
                    projectDir, // Base directory where source files can be resolved
                    sdkDirectory: null,
                    additionalReferenceDirectories: null);
            }
            else
            {
                commandLineArguments = VisualBasicCommandLineParser.Default.Parse(
                    args,
                    projectDir, // Base directory where source files can be resolved
                    sdkDirectory: null,
                    additionalReferenceDirectories: null);
            }

            // Add the compilation to the complog
            builder.AddFromDisk(compilerCall, commandLineArguments);
            AnsiConsole.MarkupLine($"  [green]✓[/] Added compilation to complog");

            // Close the builder (writes metadata and finalizes the archive)
            builder.Close();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] Error creating complog: {ex.Message}");
            AnsiConsole.MarkupLine($"  [dim]{ex.StackTrace}[/]");
            diagnostics.Add($"Error creating complog: {ex.Message}");
        }

        // Display diagnostics if any
        if (diagnostics.Count > 0)
        {
            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine($"[yellow]CompLog creation completed with {diagnostics.Count} diagnostic(s):[/]");
            foreach (var diagnostic in diagnostics.Take(10))
            {
                AnsiConsole.MarkupLine($"  [dim]• {diagnostic}[/]");
            }
            if (diagnostics.Count > 10)
            {
                AnsiConsole.MarkupLine($"  [dim]... and {diagnostics.Count - 10} more[/]");
            }
        }

        if (File.Exists(complogPath))
        {
            var fileInfo = new FileInfo(complogPath);
            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine($"[green]✓[/] CompLog file created: [cyan]{complogPath}[/]");
            AnsiConsole.MarkupLine($"  [dim]Size: {fileInfo.Length:N0} bytes[/]");
        }

        return complogPath;
    }

    private static Dictionary<string, string> ParseCompilerArgumentsFile(string[] lines)
    {
        var dict = new Dictionary<string, string>();
        var extraArgs = new List<string>();
        
        for (int i = 0; i < lines.Length; i++)
        {
            // Check if this line is a command-line argument (starts with /)
            if (lines[i].StartsWith('/'))
            {
                extraArgs.Add(lines[i]);
                continue;
            }
            
            // Otherwise treat as key-value pair
            if (i < lines.Length - 1)
            {
                dict[lines[i]] = lines[i + 1];
                i++; // Skip next line as it's the value
            }
        }
        
        // Store extra args in a special key if present
        if (extraArgs.Count > 0)
        {
            dict["__extra_args__"] = string.Join(" ", extraArgs);
        }
        
        return dict;
    }

    private static string? ExtractTargetFramework(Dictionary<string, string> args)
    {
        // Try to extract from defines
        if (args.TryGetValue("define", out var defines))
        {
            var defineList = defines.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var define in defineList)
            {
                if (define.StartsWith("NET", StringComparison.Ordinal) && define.Contains("_"))
                {
                    // Convert NET8_0 to net8.0
                    return define.Replace("_", ".").ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private static string[] BuildCompilerArguments(
        Dictionary<string, string> argsDict, 
        string assemblyPath, 
        string workingDirectory,
        Dictionary<string, string> acquiredReferences)
    {
        var args = new List<string>();
        
        // Add sources from the sources directory
        // Source files are now saved directly in sources/ with their relative structure
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        
        if (Directory.Exists(sourcesDir))
        {
            foreach (var sourceFile in Directory.GetFiles(sourcesDir, "*.cs", SearchOption.AllDirectories))
            {
                // Get path relative to sources directory to get just: JsonConvert.cs or Bson/BsonReader.cs
                var relativePath = Path.GetRelativePath(sourcesDir, sourceFile);
                args.Add(relativePath);
            }
        }

        // Add compiler options from the parsed arguments
        foreach (var kvp in argsDict)
        {
            // Skip metadata fields
            if (kvp.Key == "source-file-count" || kvp.Key == "version" || 
                kvp.Key == "compiler-version" || kvp.Key == "language" ||
                kvp.Key == "__extra_args__") 
                continue;
            
            // Map output-kind to /target
            if (kvp.Key == "output-kind")
            {
                var target = kvp.Value switch
                {
                    "ConsoleApplication" => "exe",
                    "WindowsApplication" => "winexe",
                    "DynamicallyLinkedLibrary" => "library",
                    "NetModule" => "module",
                    "WindowsRuntimeMetadata" => "winmdobj",
                    _ => "library"
                };
                args.Add($"/target:{target}");
                continue;
            }
            
            // Map optimization to the correct format (/optimize+ or /optimize-)
            if (kvp.Key == "optimization")
            {
                var optimizationValue = kvp.Value.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                                       kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                       kvp.Value.Equals("1", StringComparison.OrdinalIgnoreCase);
                args.Add($"/optimize{(optimizationValue ? "+" : "-")}");
                continue;
            }
            
            // Map known options to their correct argument names
            var argName = kvp.Key switch
            {
                "runtime-version" => "runtimemetadataversion",
                _ => kvp.Key
            };
            
            args.Add($"/{argName}:{kvp.Value}");
        }

        // Add extra command-line arguments (like /debug:embedded, /deterministic+)
        if (argsDict.TryGetValue("__extra_args__", out var extraArgs))
        {
            args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Add reference assemblies
        foreach (var reference in acquiredReferences.Values)
        {
            args.Add($"/reference:{reference}");
        }

        // Add output
        args.Add($"/out:{assemblyPath}");
        
        // Add pathmap to normalize paths in debug info  
        // Map sources/ to / so paths appear cleanly as /JsonConvert.cs in debug info
        args.Add($"/pathmap:{sourcesDir}{Path.DirectorySeparatorChar}=/");

        return args.ToArray();
    }

    private static string? FindCompilerPath(bool isCSharp)
    {
        // Try to find the Roslyn compiler in the .NET SDK
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") 
            ?? "/usr/local/share/dotnet";
        
        var sdkPath = Path.Combine(dotnetRoot, "sdk");
        if (Directory.Exists(sdkPath))
        {
            var latestSdk = Directory.GetDirectories(sdkPath)
                .OrderByDescending(d => d)
                .FirstOrDefault();
            
            if (latestSdk != null)
            {
                var compilerName = isCSharp ? "csc.dll" : "vbc.dll";
                var cscPath = Path.Combine(latestSdk, "Roslyn", "bincore", compilerName);
                if (File.Exists(cscPath))
                {
                    return cscPath;
                }
            }
        }
        
        return null;
    }

    private static List<string> FindAssemblies(string extractPath)
    {
        var assemblies = new List<string>();
        
        var libPath = Path.Combine(extractPath, "lib");
        if (Directory.Exists(libPath))
        {
            assemblies.AddRange(Directory.GetFiles(libPath, "*.dll", SearchOption.AllDirectories));
        }

        var refPath = Path.Combine(extractPath, "ref");
        if (Directory.Exists(refPath))
        {
            assemblies.AddRange(Directory.GetFiles(refPath, "*.dll", SearchOption.AllDirectories));
        }

        return assemblies;
    }
}
