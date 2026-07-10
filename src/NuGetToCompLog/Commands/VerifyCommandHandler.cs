using System.Diagnostics;
using Basic.CompilerLog.Util;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.Verify;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Proves (or disproves) that a package round-trips: creates a complog, exports it, rebuilds
/// with the exact compiler version recorded in the PDB, and byte-compares the result against
/// the assembly shipped in the package.
/// </summary>
public class VerifyCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly IConsoleWriter _console;

    public VerifyCommandHandler(PackageAnalysisPipeline pipeline, IConsoleWriter console)
    {
        _pipeline = pipeline;
        _console = console;
    }

    /// <returns>0 = byte-for-byte match, 2 = content match with derived-field drift, 1 = real differences or failure.</returns>
    public async Task<int> HandleAsync(string packageId, string? version, CancellationToken cancellationToken = default)
    {
        var result = await _pipeline.AnalyzeAsync(packageId, version, cancellationToken);
        if (result == null || result.CompilerArgsFile == null)
        {
            _console.MarkupLine("[red]✗[/] Cannot verify - no compiler arguments could be extracted from the package");
            return 1;
        }

        var complogPath = await CompLogFileCreator.CreateCompLogFileAsync(
            packageId,
            result.Package.Version,
            result.WorkingDirectory,
            result.WorkingDirectory,
            result.SelectedTfm,
            result.SelectedAssemblies);

        if (!File.Exists(complogPath))
        {
            _console.MarkupLine("[red]✗[/] Cannot verify - complog creation failed");
            return 1;
        }

        var compilerVersion = ReadCompilerVersion(result.CompilerArgsFile);
        var cscPath = FindCsc(compilerVersion);
        if (cscPath == null)
        {
            _console.MarkupLine("[red]✗[/] No csc.dll found in installed SDKs");
            return 1;
        }

        // Export the complog to a build-able directory. Everything from here on uses only the
        // complog contents - this is what proves the complog alone reproduces the assembly.
        var exportDir = Path.Combine(result.WorkingDirectory, "verify-export");
        using (var reader = CompilerLogReader.Create(complogPath))
        {
            var compilerCall = reader.ReadAllCompilerCalls().First();
            var compilerDir = Path.GetDirectoryName(cscPath)!;
            new ExportUtil(reader).Export(compilerCall, exportDir, [(compilerDir, "verify")]);
        }

        var rspPath = Path.Combine(exportDir, "build.rsp");
        if (!File.Exists(rspPath))
        {
            _console.MarkupLine("[red]✗[/] Export did not produce build.rsp");
            return 1;
        }

        MakePathMapKeysAbsolute(rspPath, exportDir);
        EnsureOutputDirectories(rspPath, exportDir);

        _console.WriteLine();
        _console.MarkupLine($"[yellow]Rebuilding from complog[/]");
        _console.MarkupLine($"  [dim]Compiler: {cscPath}[/]");
        if (compilerVersion != null && CompilerVersionReader.TryGetInformationalVersion(cscPath) is { } actualVersion &&
            !string.Equals(actualVersion, compilerVersion, StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine($"  [yellow]⚠[/] Exact compiler {compilerVersion.Split('+')[0]} is not installed; " +
                                $"using {actualVersion.Split('+')[0]} - a byte-for-byte match is unlikely");
        }

        var (exitCode, output) = await RunCscAsync(cscPath, exportDir, cancellationToken);
        if (exitCode != 0)
        {
            _console.MarkupLine($"[red]✗[/] Rebuild failed (csc exit code {exitCode}):");
            foreach (var line in output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                _console.MarkupLine($"  [dim]{line.Trim().Replace("[", "[[").Replace("]", "]]")}[/]");
            }
            return 1;
        }

        return Compare(result.SelectedAssemblies.First(), result.WorkingDirectory, result.SelectedTfm, exportDir, rspPath);
    }

    private int Compare(string originalAssembly, string workingDirectory, string? tfm, string exportDir, string rspPath)
    {
        var rspLines = File.ReadAllLines(rspPath);
        var rebuiltDll = ResolveRspPath(rspLines, "/out:", exportDir);
        var rebuiltPdb = ResolveRspPath(rspLines, "/pdb:", exportDir);

        if (rebuiltDll == null || !File.Exists(rebuiltDll))
        {
            _console.MarkupLine("[red]✗[/] Rebuild produced no output assembly");
            return 1;
        }

        _console.WriteLine();
        _console.MarkupLine("[yellow]Comparing rebuilt assembly against the package original[/]");

        var assemblyResult = BinaryDiffClassifier.CompareAssemblies(originalAssembly, rebuiltDll);
        var originalPdb = FindOriginalPdb(originalAssembly, workingDirectory, tfm);

        ComparisonResult? pdbResult = null;
        if (originalPdb != null && rebuiltPdb != null && File.Exists(rebuiltPdb))
        {
            pdbResult = BinaryDiffClassifier.ComparePdbs(originalPdb, rebuiltPdb);
        }

        if (assemblyResult.ExactMatch)
        {
            _console.MarkupLine($"[green]✓ Assembly matches byte-for-byte[/] ({Path.GetFileName(originalAssembly)})");
            if (pdbResult is { ExactMatch: true })
            {
                _console.MarkupLine("[green]✓ PDB matches byte-for-byte[/]");
            }
            return 0;
        }

        if (assemblyResult.DerivedOnly)
        {
            _console.MarkupLine("[yellow]≈ Assembly content matches[/] - only derived fields differ:");
            foreach (var diff in assemblyResult.DerivedDifferences)
            {
                _console.MarkupLine($"  [dim]• {diff}[/]");
            }
            _console.MarkupLine("[dim]  Derived fields (MVID, timestamps, PDB id, signature) trail the PDB and signing key;[/]");
            _console.MarkupLine("[dim]  the causes below explain the remaining drift.[/]");
        }
        else
        {
            _console.MarkupLine("[red]✗ Assembly has real content differences:[/]");
            foreach (var diff in assemblyResult.RealDifferences.Take(10))
            {
                _console.MarkupLine($"  [dim]• {diff}[/]");
            }
        }

        if (pdbResult != null && !pdbResult.ExactMatch)
        {
            _console.MarkupLine("[yellow]PDB differences:[/]");
            foreach (var finding in pdbResult.RealDifferences.Where(f => !f.StartsWith("bytes differ")).Take(10))
            {
                _console.MarkupLine($"  [dim]• {finding}[/]");
            }
        }

        return assemblyResult.DerivedOnly ? 2 : 1;
    }

    private static string? ResolveRspPath(string[] rspLines, string prefix, string exportDir)
    {
        var value = rspLines
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..].Trim('"');
        if (value == null)
        {
            return null;
        }
        return Path.IsPathRooted(value) ? value : Path.Combine(exportDir, value);
    }

    private string? FindOriginalPdb(string assemblyPath, string workingDirectory, string? tfm)
    {
        var pdbName = Path.GetFileNameWithoutExtension(assemblyPath) + ".pdb";

        var next = Path.Combine(Path.GetDirectoryName(assemblyPath)!, pdbName);
        if (File.Exists(next))
        {
            return next;
        }

        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (!Directory.Exists(symbolsDir))
        {
            return null;
        }

        var candidates = Directory.GetFiles(symbolsDir, pdbName, SearchOption.AllDirectories);
        return candidates.FirstOrDefault(c => tfm != null && c.Contains($"{Path.DirectorySeparatorChar}{tfm}{Path.DirectorySeparatorChar}"))
               ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// csc normalizes source paths to absolute before applying /pathmap, so the relative keys
    /// the export layout uses ("src/", "output/") never match. Anchor them to the export dir.
    /// </summary>
    private static void MakePathMapKeysAbsolute(string rspPath, string exportDir)
    {
        var lines = File.ReadAllLines(rspPath);
        for (var i = 0; i < lines.Length; i++)
        {
            const string prefix = "/pathmap:";
            if (!lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mapping = lines[i][prefix.Length..].Trim('"');
            var separator = mapping.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = mapping[..separator];
            if (!Path.IsPathRooted(key))
            {
                key = Path.Combine(exportDir, key);
                if (mapping[separator - 1] is '/' or '\\' && !key.EndsWith(Path.DirectorySeparatorChar))
                {
                    key += Path.DirectorySeparatorChar;
                }
            }

            lines[i] = $"{prefix}\"{key}={mapping[(separator + 1)..]}\"";
        }
        File.WriteAllLines(rspPath, lines);
    }

    /// <summary>
    /// csc doesn't create directories for its outputs (CS2012); make sure every output path's
    /// parent exists before invoking it.
    /// </summary>
    private static void EnsureOutputDirectories(string rspPath, string exportDir)
    {
        var lines = File.ReadAllLines(rspPath);
        foreach (var prefix in new[] { "/out:", "/pdb:", "/doc:", "/refout:" })
        {
            var value = ResolveRspPath(lines, prefix, exportDir);
            if (value != null && Path.GetDirectoryName(value) is { } dir)
            {
                Directory.CreateDirectory(dir);
            }
        }
    }

    private static string? ReadCompilerVersion(string compilerArgsFile)
    {
        var lines = File.ReadAllLines(compilerArgsFile);
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i] == "compiler-version")
            {
                return lines[i + 1];
            }
        }
        return null;
    }

    private static string? FindCsc(string? compilerVersion)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/local/share/dotnet";
        var sdkPath = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkPath))
        {
            return null;
        }

        var candidates = Directory.GetDirectories(sdkPath)
            .OrderByDescending(d => d)
            .Select(sdk => Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"))
            .Where(File.Exists)
            .ToList();

        if (!string.IsNullOrEmpty(compilerVersion))
        {
            var exact = candidates.FirstOrDefault(c =>
                string.Equals(CompilerVersionReader.TryGetInformationalVersion(c), compilerVersion, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        return candidates.FirstOrDefault();
    }

    private static async Task<(int ExitCode, string Output)> RunCscAsync(
        string cscPath, string exportDir, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = exportDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cscPath);
        startInfo.ArgumentList.Add("@build.rsp");

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout + stderr);
    }
}
