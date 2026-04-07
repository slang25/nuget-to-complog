using System.Text.Json;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services;
using Microsoft.CodeAnalysis;

namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Generates a buildable project from a package extraction result.
/// Produces a response file (.rsp), source files, references, and rebuild scripts.
/// </summary>
public class ProjectGenerator
{
    private readonly IConsoleWriter _console;

    public ProjectGenerator(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Generates an editable project from a package extraction result.
    /// Returns the path to the patch directory.
    /// </summary>
    public async Task<string> GenerateAsync(
        PackageExtractionResult extraction,
        string patchesBaseDir)
    {
        var patchDir = Path.Combine(patchesBaseDir, $"{extraction.Package.Id}+{extraction.Package.Version}");

        if (Directory.Exists(patchDir))
        {
            _console.MarkupLine($"[yellow]\u26a0[/] Patch directory already exists: [dim]{patchDir}[/]");
            _console.MarkupLine("[yellow]\u26a0[/] Remove it first if you want to re-eject");
            return patchDir;
        }

        Directory.CreateDirectory(patchDir);

        var srcDir = Path.Combine(patchDir, "src");
        var originalDir = Path.Combine(patchDir, ".original");
        var refsDir = Path.Combine(patchDir, "refs");
        var resourcesDir = Path.Combine(patchDir, "resources");
        var binDir = Path.Combine(patchDir, "bin");

        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(originalDir);
        Directory.CreateDirectory(refsDir);
        Directory.CreateDirectory(binDir);

        // Copy source files
        var sourceCount = 0;
        if (Directory.Exists(extraction.SourcesDirectory))
        {
            foreach (var sourceFile in Directory.GetFiles(extraction.SourcesDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(extraction.SourcesDirectory, sourceFile);
                var srcDest = Path.Combine(srcDir, relativePath);
                var origDest = Path.Combine(originalDir, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(srcDest)!);
                Directory.CreateDirectory(Path.GetDirectoryName(origDest)!);

                File.Copy(sourceFile, srcDest, overwrite: true);
                File.Copy(sourceFile, origDest, overwrite: true);
                sourceCount++;
            }
        }
        _console.MarkupLine($"  [green]\u2713[/] Copied {sourceCount} source files to src/ and .original/");

        // Copy resources
        var resourceCount = 0;
        if (extraction.ResourcesDirectory != null && Directory.Exists(extraction.ResourcesDirectory))
        {
            Directory.CreateDirectory(resourcesDir);
            foreach (var resourceFile in Directory.GetFiles(extraction.ResourcesDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(extraction.ResourcesDirectory, resourceFile);
                var dest = Path.Combine(resourcesDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(resourceFile, dest, overwrite: true);
                resourceCount++;
            }

            // Copy resource mappings if they exist
            var mappingsFile = Path.Combine(extraction.WorkingDirectory, "resource-mappings.txt");
            if (File.Exists(mappingsFile))
            {
                File.Copy(mappingsFile, Path.Combine(patchDir, "resource-mappings.txt"), overwrite: true);
            }
        }
        if (resourceCount > 0)
        {
            _console.MarkupLine($"  [green]\u2713[/] Copied {resourceCount} resource files");
        }

        // Acquire and copy references
        var refCount = await AcquireReferencesAsync(extraction, refsDir);
        _console.MarkupLine($"  [green]\u2713[/] Acquired {refCount} reference assemblies");

        // Generate build.rsp
        var assemblyName = extraction.SelectedAssemblies.Count > 0
            ? Path.GetFileNameWithoutExtension(extraction.SelectedAssemblies[0])
            : extraction.Package.Id;

        await GenerateBuildRspAsync(extraction, patchDir, assemblyName);
        _console.MarkupLine($"  [green]\u2713[/] Generated build.rsp");

        // Generate rebuild scripts
        await GenerateRebuildScriptsAsync(patchDir, assemblyName);
        _console.MarkupLine($"  [green]\u2713[/] Generated rebuild scripts");

        // Write patch metadata
        await WritePatchMetadataAsync(extraction, patchDir, assemblyName);
        _console.MarkupLine($"  [green]\u2713[/] Wrote patch-metadata.json");

        return patchDir;
    }

    private async Task<int> AcquireReferencesAsync(PackageExtractionResult extraction, string refsDir)
    {
        var count = 0;

        if (extraction.MetadataRefsFile == null || extraction.SelectedTfm == null)
            return count;

        var refLines = await File.ReadAllLinesAsync(extraction.MetadataRefsFile);
        var metadataReferences = refLines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => new MetadataReference(
                FileName: line,
                ExternAliases: [],
                EmbedInteropTypes: false,
                Kind: MetadataImageKind.Assembly,
                Timestamp: 0,
                ImageSize: 0,
                Mvid: Guid.Empty))
            .ToList();

        if (metadataReferences.Count == 0)
            return count;

        var acquisitionService = new ReferenceAssemblyAcquisitionService(extraction.WorkingDirectory);
        var acquiredReferences = await acquisitionService.AcquireAllReferencesAsync(metadataReferences, extraction.SelectedTfm);

        foreach (var (name, path) in acquiredReferences)
        {
            var dest = Path.Combine(refsDir, Path.GetFileName(path));
            if (!File.Exists(dest))
            {
                File.Copy(path, dest, overwrite: true);
                count++;
            }
        }

        return count;
    }

    private async Task GenerateBuildRspAsync(
        PackageExtractionResult extraction,
        string patchDir,
        string assemblyName)
    {
        var lines = new List<string>();

        // Read compiler arguments from extraction
        if (extraction.CompilerArgsFile != null)
        {
            var compilerArgs = await File.ReadAllLinesAsync(extraction.CompilerArgsFile);
            var (argsDict, extraArgs) = ParseCompilerArgumentsFile(compilerArgs);

            // Compiler flags (preserved as-is to avoid breaking quoted values)
            foreach (var arg in extraArgs)
            {
                lines.Add(arg);
            }

            if (argsDict.TryGetValue("define", out var defines))
            {
                lines.Add($"/define:{defines.Replace(",", ";")}");
            }

            // Analyze debug config from primary assembly
            if (extraction.SelectedAssemblies.Count > 0)
            {
                var debugConfig = DebugConfigurationExtractor.ExtractDebugConfiguration(extraction.SelectedAssemblies[0]);
                if (debugConfig.HighEntropyVA)
                {
                    lines.Add("/highentropyva+");
                }
                // Use portable debug for the rebuild
                lines.Add("/debug:portable");
                lines.Add("/filealign:512");

                if (argsDict.TryGetValue("optimization", out var optimization))
                {
                    var optimizationValue = optimization.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                                           optimization.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                           optimization.Equals("1", StringComparison.OrdinalIgnoreCase);
                    lines.Add($"/optimize{(optimizationValue ? "+" : "-")}");
                }

                if (debugConfig.HasReproducible)
                {
                    lines.Add("/deterministic+");
                }
            }

            if (argsDict.TryGetValue("output-kind", out var outputKind))
            {
                var target = outputKind switch
                {
                    "ConsoleApplication" => "exe",
                    "WindowsApplication" => "winexe",
                    "DynamicallyLinkedLibrary" => "library",
                    "NetModule" => "module",
                    _ => "library"
                };
                lines.Add($"/target:{target}");
            }

            lines.Add("/utf8output");

            if (argsDict.TryGetValue("language-version", out var langVersion))
            {
                lines.Add($"/langversion:{langVersion}");
            }

            // Additional args that weren't already handled
            foreach (var kvp in argsDict)
            {
                if (kvp.Key is "source-file-count" or "version" or "compiler-version"
                    or "language" or "define" or "optimization"
                    or "output-kind" or "language-version")
                    continue;

                var argName = kvp.Key switch
                {
                    "runtime-version" => "runtimemetadataversion",
                    _ => kvp.Key
                };

                lines.Add($"/{argName}:{kvp.Value}");
            }
        }

        // Source files - relative to the patch directory
        lines.Add("");
        lines.Add("# Source files");
        var srcDir = Path.Combine(patchDir, "src");
        if (Directory.Exists(srcDir))
        {
            foreach (var sourceFile in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(patchDir, sourceFile);
                lines.Add(relativePath);
            }
        }

        // Resources
        var resourceMappingsFile = Path.Combine(patchDir, "resource-mappings.txt");
        if (File.Exists(resourceMappingsFile))
        {
            lines.Add("");
            lines.Add("# Resources");
            var mappings = await File.ReadAllLinesAsync(resourceMappingsFile);
            foreach (var mapping in mappings)
            {
                var parts = mapping.Split('|');
                if (parts.Length == 2)
                {
                    lines.Add($"/resource:resources/{parts[0]},{parts[1]}");
                }
            }
        }

        // References
        var refsDir = Path.Combine(patchDir, "refs");
        if (Directory.Exists(refsDir))
        {
            var refFiles = Directory.GetFiles(refsDir, "*.dll")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            lines.Add("");
            lines.Add("# References");
            foreach (var refFile in refFiles)
            {
                lines.Add($"/reference:refs/{Path.GetFileName(refFile)}");
            }
        }

        // Output
        lines.Add("");
        lines.Add("# Output");
        lines.Add($"/out:bin/{assemblyName}.dll");

        await File.WriteAllLinesAsync(Path.Combine(patchDir, "build.rsp"), lines);
    }

    private async Task GenerateRebuildScriptsAsync(string patchDir, string assemblyName)
    {
        // Unix script
        var shScript = "#!/bin/bash\n"
            + "set -e\n"
            + "cd \"$(dirname \"$0\")\"\n"
            + $"echo \"Rebuilding {assemblyName}...\"\n"
            + "\n"
            + "# Find csc.dll from the .NET SDK\n"
            + "DOTNET_ROOT=\"${DOTNET_ROOT:-/usr/local/share/dotnet}\"\n"
            + "LATEST_SDK=$(ls -d \"$DOTNET_ROOT/sdk/\"*/ 2>/dev/null | sort -V | tail -1)\n"
            + "CSC_DLL=\"${LATEST_SDK}Roslyn/bincore/csc.dll\"\n"
            + "\n"
            + "if [ ! -f \"$CSC_DLL\" ]; then\n"
            + "    echo \"Error: Could not find csc.dll at $CSC_DLL\"\n"
            + "    echo \"Set DOTNET_ROOT to your .NET SDK installation path\"\n"
            + "    exit 1\n"
            + "fi\n"
            + "\n"
            + "dotnet exec \"$CSC_DLL\" @build.rsp\n"
            + $"echo \"Built: bin/{assemblyName}.dll\"\n";
        var shPath = Path.Combine(patchDir, "rebuild.sh");
        await File.WriteAllTextAsync(shPath, shScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Windows script
        var cmdScript = "@echo off\r\n"
            + "cd /d \"%~dp0\"\r\n"
            + $"echo Rebuilding {assemblyName}...\r\n"
            + "\r\n"
            + "REM Find csc.dll from the .NET SDK\r\n"
            + "if \"%DOTNET_ROOT%\"==\"\" set \"DOTNET_ROOT=%ProgramFiles%\\dotnet\"\r\n"
            + "for /f \"delims=\" %%d in ('dir /b /ad /o-n \"%DOTNET_ROOT%\\sdk\" 2^>nul') do (\r\n"
            + "    set \"LATEST_SDK=%DOTNET_ROOT%\\sdk\\%%d\"\r\n"
            + "    goto :found\r\n"
            + ")\r\n"
            + "echo Error: Could not find .NET SDK in %DOTNET_ROOT%\\sdk\r\n"
            + "exit /b 1\r\n"
            + ":found\r\n"
            + "set \"CSC_DLL=%LATEST_SDK%\\Roslyn\\bincore\\csc.dll\"\r\n"
            + "if not exist \"%CSC_DLL%\" (\r\n"
            + "    echo Error: Could not find csc.dll at %CSC_DLL%\r\n"
            + "    exit /b 1\r\n"
            + ")\r\n"
            + "\r\n"
            + "dotnet exec \"%CSC_DLL%\" @build.rsp\r\n"
            + $"echo Built: bin\\{assemblyName}.dll\r\n";
        await File.WriteAllTextAsync(Path.Combine(patchDir, "rebuild.cmd"), cmdScript);
    }

    private async Task WritePatchMetadataAsync(
        PackageExtractionResult extraction,
        string patchDir,
        string assemblyName)
    {
        var metadata = new
        {
            packageId = extraction.Package.Id,
            version = extraction.Package.Version,
            targetFramework = extraction.SelectedTfm,
            assemblyName,
            assemblies = extraction.SelectedAssemblies.Select(Path.GetFileName).ToList(),
            ejectedAt = DateTime.UtcNow.ToString("o"),
            toolVersion = "1.0.0"
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(patchDir, "patch-metadata.json"), json);
    }

    private static (Dictionary<string, string> Args, List<string> ExtraArgs) ParseCompilerArgumentsFile(string[] lines)
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

        return (dict, extraArgs);
    }
}
