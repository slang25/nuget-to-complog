using Basic.CompilerLog.Util;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
using Spectre.Console;
using NuGetToCompLog.Services;

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
        string? overrideTfm = null,
        List<string>? selectedAssemblies = null)
    {
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[yellow]Creating .complog file...[/]");

        var complogPath = Path.Combine(outputDirectory, $"{packageId}.{version}.complog");
        
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
            
            using var builder = new CompilerLogBuilder(complogStream, diagnostics);

            var assemblies = selectedAssemblies ?? new List<string>();
            
            if (assemblies.Count == 0)
            {
                var referencesDir = Path.Combine(workingDirectory, "references");
                var extractedDir = Path.Combine(workingDirectory, "extracted");
                
                AnsiConsole.MarkupLine($"  [dim]Working directory: {workingDirectory}[/]");
                AnsiConsole.MarkupLine($"  [dim]References dir: {referencesDir}, exists: {Directory.Exists(referencesDir)}[/]");
                
                if (Directory.Exists(referencesDir))
                {
                    assemblies.AddRange(Directory.GetFiles(referencesDir, "*.dll", SearchOption.TopDirectoryOnly));
                    if (assemblies.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"  [green]Using assembly from references:[/] {Path.GetFileName(assemblies[0])}");
                    }
                }
                
                if (assemblies.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [yellow]Falling back to extracted directory[/]");
                    assemblies = FindAssemblies(extractedDir);
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"  [green]Using pre-selected assemblies[/] ({assemblies.Count})");
            }
            
            if (assemblies.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] No assemblies found in package");
                return complogPath;
            }

            var assemblyPath = assemblies[0];
            var isCSharp = !assemblyPath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

            AnsiConsole.MarkupLine($"  [cyan]Analyzing assembly:[/] {Path.GetRelativePath(workingDirectory, assemblyPath)}");
            var debugConfig = DebugConfigurationExtractor.ExtractDebugConfiguration(assemblyPath);
            AnsiConsole.MarkupLine($"  [cyan]Debug configuration:[/] {debugConfig.DebugType}");
            AnsiConsole.MarkupLine($"    [dim]{debugConfig}[/]");
            if (!string.IsNullOrEmpty(debugConfig.PdbPath))
            {
                AnsiConsole.MarkupLine($"    [dim]PDB Path: {debugConfig.PdbPath}[/]");
            }

            var compilerPath = FindCompilerPath(isCSharp);
            var argsDict = ParseCompilerArgumentsFile(compilerArgs);
            var targetFramework = overrideTfm ?? ExtractTargetFramework(argsDict);
            
            if (overrideTfm != null && overrideTfm != ExtractTargetFramework(argsDict))
            {
                AnsiConsole.MarkupLine($"  [cyan]→[/] Overriding TFM: [yellow]{ExtractTargetFramework(argsDict)}[/] → [green]{overrideTfm}[/]");
                AnsiConsole.MarkupLine($"     [dim](Using selected package TFM instead of PDB TFM)[/]");
            }
            
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

            Dictionary<string, string> acquiredReferences = new();
            if (metadataReferences.Count > 0 && !string.IsNullOrEmpty(targetFramework))
            {
                var acquisitionService = new ReferenceAssemblyAcquisitionService(workingDirectory);
                acquiredReferences = await acquisitionService.AcquireAllReferencesAsync(metadataReferences, targetFramework);
                
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

            var args = BuildCompilerArguments(argsDict, assemblyPath, workingDirectory, acquiredReferences, debugConfig);

            var projectDir = Path.Combine(workingDirectory, "sources");
            var projectFilePath = Path.Combine(projectDir, $"{packageId}.csproj");
            
            var compilerCall = new CompilerCall(
                projectFilePath: projectFilePath,
                compilerFilePath: compilerPath,
                kind: CompilerCallKind.Regular,
                targetFramework: targetFramework,
                isCSharp: isCSharp);

            // CommandLineArguments commandLineArguments;
            //
            // if (isCSharp)
            // {
            //     commandLineArguments = CSharpCommandLineParser.Default.Parse(
            //         args,
            //         projectDir,
            //         sdkDirectory: null,
            //         additionalReferenceDirectories: null);
            // }
            // else
            // {
            //     commandLineArguments = VisualBasicCommandLineParser.Default.Parse(
            //         args,
            //         projectDir,
            //         sdkDirectory: null,
            //         additionalReferenceDirectories: null);
            // }

            
            builder.AddFromDisk(compilerCall, args);
            AnsiConsole.MarkupLine($"  [green]✓[/] Added compilation to complog");

            builder.Close();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✗[/] Error creating complog: {ex.Message}");
            AnsiConsole.MarkupLine($"  [dim]{ex.StackTrace}[/]");
            diagnostics.Add($"Error creating complog: {ex.Message}");
        }

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
            if (lines[i].StartsWith('/'))
            {
                extraArgs.Add(lines[i]);
                continue;
            }
            
            if (i < lines.Length - 1)
            {
                dict[lines[i]] = lines[i + 1];
                i++;
            }
        }
        
        if (extraArgs.Count > 0)
        {
            dict["__extra_args__"] = string.Join(" ", extraArgs);
        }
        
        return dict;
    }

    private static string? ExtractTargetFramework(Dictionary<string, string> args)
    {
        if (args.TryGetValue("define", out var defines))
        {
            var defineList = defines.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var define in defineList)
            {
                if (define.StartsWith("NET", StringComparison.Ordinal) && define.Contains("_"))
                {
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
        Dictionary<string, string> acquiredReferences,
        DebugConfiguration debugConfig)
    {
        var args = new List<string>();
        
        // Step 1: Add compiler flags in proper order to match csc.exe
        // These are typically at the beginning of the argument list
        
        // Basic compiler flags (from __extra_args__)
        if (argsDict.TryGetValue("__extra_args__", out var extraArgs))
        {
            args.AddRange(extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        
        // /define: - Note: use semicolon separator to match original builds
        if (argsDict.TryGetValue("define", out var defines))
        {
            args.Add($"/define:{defines.Replace(",", ";")}");
        }
        
        // /highentropyva+ comes before debug flags
        if (debugConfig.HighEntropyVA)
        {
            args.Add("/highentropyva+");
        }
        
        // Debug flags
        string? pdbOutputPath = null;
        if (debugConfig.DebugType == DebugType.PortableExternal && !string.IsNullOrEmpty(debugConfig.PdbPath))
        {
            var pdbFileName = Path.GetFileName(debugConfig.PdbPath);
            pdbOutputPath = $"output/{pdbFileName}";
        }
        var debugFlags = debugConfig.ToCompilerFlags(pdbOutputPath).Where(f => !f.StartsWith("/highentropyva"));
        args.AddRange(debugFlags);
        
        // /filealign:
        args.Add("/filealign:512");
        
        // /optimize
        if (argsDict.TryGetValue("optimization", out var optimization))
        {
            var optimizationValue = optimization.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                                   optimization.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                   optimization.Equals("1", StringComparison.OrdinalIgnoreCase);
            args.Add($"/optimize{(optimizationValue ? "+" : "-")}");
        }
        
        // Pathmap (must come before /target to match ordering)
        if (!string.IsNullOrEmpty(debugConfig.PdbPath))
        {
            var pdbDir = Path.GetDirectoryName(debugConfig.PdbPath);
            if (!string.IsNullOrEmpty(pdbDir))
            {
                args.Add($"/pathmap:output/={pdbDir}/");
                var pdbPathParts = debugConfig.PdbPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pdbPathParts.Length >= 3 && pdbPathParts[0] == "_" && pdbPathParts[1] == "src")
                {
                    var packageName = pdbPathParts[2];
                    args.Add($"/pathmap:src/=/_/src/{packageName}/");
                }
            }
        }
        else
        {
            args.Add($"/pathmap:src/=/");
        }
        
        // /target:
        if (argsDict.TryGetValue("output-kind", out var outputKind))
        {
            var target = outputKind switch
            {
                "ConsoleApplication" => "exe",
                "WindowsApplication" => "winexe",
                "DynamicallyLinkedLibrary" => "library",
                "NetModule" => "module",
                "WindowsRuntimeMetadata" => "winmdobj",
                _ => "library"
            };
            args.Add($"/target:{target}");
        }
        
        // /warnaserror+ (if applicable)
        args.Add("/warnaserror+");
        
        // /utf8output
        args.Add("/utf8output");
        
        // /deterministic+ - essential for reproducible builds (moved here to match original order)
        if (debugConfig.HasReproducible)
        {
            args.Add("/deterministic+");
        }
        
        // /langversion:
        if (argsDict.TryGetValue("language-version", out var langVersion))
        {
            args.Add($"/langversion:{langVersion}");
        }
        
        // /features:strict
        args.Add("/features:strict");
        
        // Additional metadata args
        foreach (var kvp in argsDict)
        {
            // Skip already processed or metadata-only keys
            if (kvp.Key == "source-file-count" || kvp.Key == "version" || 
                kvp.Key == "compiler-version" || kvp.Key == "language" ||
                kvp.Key == "__extra_args__" || kvp.Key == "define" ||
                kvp.Key == "optimization" || kvp.Key == "output-kind" ||
                kvp.Key == "language-version") 
                continue;
            
            var argName = kvp.Key switch
            {
                "runtime-version" => "runtimemetadataversion",
                _ => kvp.Key
            };
            
            args.Add($"/{argName}:{kvp.Value}");
        }
        
        // Step 2: Add source files
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        if (Directory.Exists(sourcesDir))
        {
            var sourceFiles = Directory.GetFiles(sourcesDir, "*.cs", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(sourcesDir, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            
            foreach (var sourceFile in sourceFiles)
            {
                args.Add(sourceFile);
            }
        }
        
        // Step 3: Add embedded resource files
        var resourcesDir = Path.Combine(workingDirectory, "resources");
        var resourceMappingsFile = Path.Combine(workingDirectory, "resource-mappings.txt");
        
        if (Directory.Exists(resourcesDir) && File.Exists(resourceMappingsFile))
        {
            var mappings = File.ReadAllLines(resourceMappingsFile);
            
            foreach (var mapping in mappings)
            {
                var parts = mapping.Split('|');
                if (parts.Length == 2)
                {
                    var sanitizedName = parts[0];
                    var originalName = parts[1];
                    var resourceFile = Path.Combine(resourcesDir, sanitizedName);
                    
                    if (File.Exists(resourceFile))
                    {
                        args.Add($"/resource:{resourceFile},{originalName}");
                    }
                }
            }
        }
        
        // Step 4: Add references (sorted alphabetically)
        var sortedReferences = acquiredReferences.Values
            .OrderBy(r => Path.GetFileName(r), StringComparer.OrdinalIgnoreCase)
            .ToList();

        
        foreach (var reference in sortedReferences)
        {
            args.Add($"/reference:{reference}");
        }

        // Step 5: Add output arguments
        var outputPath = assemblyPath;
        var outputFileName = Path.GetFileNameWithoutExtension(outputPath);
        
        // Add /doc: argument for XML documentation output
        args.Add($"/doc:output/{outputFileName}.xml");
        
        args.Add($"/out:{outputPath}");
        
        // Add /refout: argument for reference assembly output
        // Reference assemblies are typically output to a separate directory
        args.Add($"/refout:output/group0/{outputFileName}.dll");

        return args.ToArray();
    }

    private static string? FindCompilerPath(bool isCSharp)
    {
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
