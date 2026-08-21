using System.Diagnostics;
using Basic.CompilerLog.Util;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.Reconstruction;
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
    private readonly CompilerToolsetService _toolset;
    private readonly IConsoleWriter _console;

    public VerifyCommandHandler(PackageAnalysisPipeline pipeline, CompilerToolsetService toolset, IConsoleWriter console)
    {
        _pipeline = pipeline;
        _toolset = toolset;
        _console = console;
    }

    /// <returns>0 = byte-for-byte match, 2 = content match with derived-field drift, 1 = real differences or failure.</returns>
    public async Task<int> HandleAsync(string packageId, string? version, bool fetchCompiler = false, CancellationToken cancellationToken = default)
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
            result.SelectedAssemblies,
            result.Ledger);

        if (!File.Exists(complogPath))
        {
            _console.MarkupLine("[red]✗[/] Cannot verify - complog creation failed");
            return 1;
        }

        var compilerVersion = ReadPdbOption(result.CompilerArgsFile, "compiler-version");
        var cscPath = FindCsc(compilerVersion);

        // Opt-in: when no installed SDK has the exact compiler, fetch it as the matching
        // Microsoft.Net.Compilers.Toolset package instead of settling for the newest local one.
        if (fetchCompiler && compilerVersion != null && !IsExactCompiler(cscPath, compilerVersion))
        {
            _console.MarkupLine($"  [yellow]Exact compiler {compilerVersion.Split('+')[0]} is not installed - " +
                                "fetching Microsoft.Net.Compilers.Toolset...[/]");
            var downloaded = await _toolset.TryGetCscAsync(compilerVersion, cancellationToken);
            if (downloaded != null)
            {
                cscPath = downloaded;
            }
            else
            {
                _console.MarkupLine($"  [yellow]⚠[/] Version {compilerVersion.Split('+')[0]} is not available on " +
                                    "nuget.org or the dnceng dotnet-tools feed (older builds age out of retention)");
            }
        }

        if (cscPath == null)
        {
            _console.MarkupLine("[red]✗[/] No csc.dll found in installed SDKs");
            return 1;
        }

        // Export the complog to a build-able directory. Everything from here on uses only the
        // complog contents - this is what proves the complog alone reproduces the assembly.
        var exportDir = Path.Combine(result.WorkingDirectory, "verify-export");
        // A package can ship several assemblies (NUnit: nunit.framework + nunit.framework.legacy),
        // giving the complog one compilation each. Export the one that actually built the
        // assembly being compared, or the comparison reports one assembly's bytes against
        // another's and every difference is meaningless.
        var originalAssembly = result.SelectedAssemblies.First();
        using (var reader = CompilerLogReader.Create(complogPath))
        {
            var compilerCalls = reader.ReadAllCompilerCalls();
            var assemblyName = Path.GetFileNameWithoutExtension(originalAssembly);
            var compilerCall = compilerCalls.FirstOrDefault(c =>
                    string.Equals(Path.GetFileNameWithoutExtension(c.ProjectFileName), assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                ?? compilerCalls.First();

            if (compilerCalls.Count > 1)
            {
                _console.MarkupLine(
                    $"  [dim]Package builds {compilerCalls.Count} assemblies; verifying " +
                    $"{Path.GetFileName(originalAssembly)}[/]");
            }

            var compilerDir = Path.GetDirectoryName(cscPath)!;
            new ExportUtil(reader).Export(compilerCall, exportDir, [(compilerDir, "verify")]);
        }

        // csc resolves the rsp's relative source paths against its working directory as the OS
        // reports it, with symlinks resolved (macOS: /var -> /private/var, /tmp -> /private/tmp).
        // Pathmap keys must be built from that canonical form or they never match and every
        // document keeps its machine-local absolute path.
        exportDir = CanonicalizeDirectory(exportDir);

        var rspPath = Path.Combine(exportDir, "build.rsp");
        if (!File.Exists(rspPath))
        {
            _console.MarkupLine("[red]✗[/] Export did not produce build.rsp");
            return 1;
        }

        MakePathMapKeysAbsolute(rspPath, exportDir);
        MapGeneratedFilesOut(rspPath, exportDir, result.WorkingDirectory);
        EnsureOutputDirectories(rspPath, exportDir);

        _console.WriteLine();
        _console.MarkupLine($"[yellow]Rebuilding from complog[/]");
        _console.MarkupLine($"  [dim]Compiler: {cscPath}[/]");
        // The complog was built before a compiler was chosen, so its ledger entry was a
        // prediction; this is what the rebuild actually ran.
        if (compilerVersion != null)
        {
            var actual = CompilerVersionReader.TryGetInformationalVersion(cscPath);
            if (actual != null && !string.Equals(actual, compilerVersion, StringComparison.OrdinalIgnoreCase))
            {
                _console.MarkupLine($"  [yellow]⚠[/] Exact compiler {compilerVersion.Split('+')[0]} is not installed; " +
                                    $"using {actual.Split('+')[0]} - a byte-for-byte match is unlikely");
                result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, compilerVersion.Split('+')[0],
                    InputEvidence.Assumed,
                    $"rebuilt with {actual.Split('+')[0]} instead - codegen and generated-document " +
                    "checksums differ between compiler versions");
            }
            else
            {
                result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, compilerVersion.Split('+')[0],
                    InputEvidence.Proven,
                    "the exact compiler recorded in the PDB, verified by its informational version");
            }
        }

        // The PDB's compilation options record the runtime that hosted the original compiler
        // ("runtime-version"). Rebuilding on a different runtime changes that blob (and through
        // the PdbChecksum every derived field), so run csc on the same runtime when installed.
        var runtimeVersion = ReadPdbOption(result.CompilerArgsFile, "runtime-version");
        var fxVersion = FindInstalledRuntime(runtimeVersion);
        if (runtimeVersion != null && fxVersion == null)
        {
            _console.MarkupLine($"  [yellow]⚠[/] Runtime {runtimeVersion.Split('+')[0]} that hosted the original compiler " +
                                "is not installed; the PDB compilation-options blob will differ");
            result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, "runtime", InputEvidence.Assumed,
                $"the runtime {runtimeVersion.Split('+')[0]} that hosted the original compiler is not " +
                "installed, and it is recorded in the compilation-options blob");
        }
        else if (runtimeVersion != null)
        {
            result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, "runtime", InputEvidence.Proven,
                $"csc hosted on {runtimeVersion.Split('+')[0]}, the runtime recorded in the options blob");
        }

        var ledgerPath = Path.Combine(result.WorkingDirectory, $"{packageId}.{result.Package.Version}.reconstruction.json");
        await result.Ledger.SaveAsync(ledgerPath);

        var (exitCode, output) = await RunCscAsync(cscPath, fxVersion, exportDir, cancellationToken);
        if (exitCode != 0)
        {
            _console.MarkupLine($"[red]✗[/] Rebuild failed (csc exit code {exitCode}):");
            foreach (var line in output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                _console.MarkupLine($"  [dim]{line.Trim().Replace("[", "[[").Replace("]", "]]")}[/]");
            }
            return 1;
        }

        return Compare(originalAssembly, result.WorkingDirectory, result.SelectedTfm, exportDir, rspPath);
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
        else
        {
            // /debug:embedded builds carry the PDB inside the assembly; extract both so the
            // PDB-level causes get explained instead of showing up as opaque byte ranges.
            var originalExtracted = Path.Combine(exportDir, "original.embedded.pdb");
            var rebuiltExtracted = Path.Combine(exportDir, "rebuilt.embedded.pdb");
            if (BinaryDiffClassifier.TryExtractEmbeddedPdb(originalAssembly, originalExtracted) &&
                BinaryDiffClassifier.TryExtractEmbeddedPdb(rebuiltDll, rebuiltExtracted))
            {
                pdbResult = BinaryDiffClassifier.ComparePdbs(originalExtracted, rebuiltExtracted);
            }
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
            // Raw byte clusters say nothing actionable about a PDB, so only the explained
            // findings are listed - but say so rather than printing an empty section, which
            // reads as "the PDB matched".
            var findings = pdbResult.RealDifferences.Where(f => !f.StartsWith("bytes differ")).Take(10).ToList();
            if (findings.Count > 0)
            {
                _console.MarkupLine("[yellow]PDB differences:[/]");
                foreach (var finding in findings)
                {
                    _console.MarkupLine($"  [dim]• {finding}[/]");
                }
            }
            else
            {
                _console.MarkupLine("[yellow]PDB differs[/] [dim]- no attributable cause found[/]");
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
    /// ExportUtil relocates the /generatedfilesout directory into its own output/ layout, which
    /// escapes the src/ pathmap. Map that directory back to the original obj/ root (project
    /// pathmap root + the generated docs' obj prefix from the manifest) so generator-produced
    /// documents keep their original paths.
    /// </summary>
    private static void MapGeneratedFilesOut(string rspPath, string exportDir, string workingDirectory)
    {
        var lines = File.ReadAllLines(rspPath).ToList();
        const string prefix = "/generatedfilesout:";
        var index = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var manifest = SourceManifest.TryLoad(workingDirectory);
        if (manifest?.PathMapRoot == null)
        {
            return;
        }
        var generatedBase = manifest.Documents
            .Select(d => System.Text.RegularExpressions.Regex.Match(
                d.LocalPath.Replace('\\', '/'), @"^((?:[^/]+/)*obj/[^/]+/[^/]+)/[^/]+/[^/]+/[^/]+$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
        if (generatedBase == null)
        {
            return;
        }

        var value = lines[index][prefix.Length..].Trim('"');
        var absolute = Path.IsPathRooted(value) ? value : Path.Combine(exportDir, value);
        lines[index] = $"{prefix}\"{absolute}\"";

        // csc applies the first matching pathmap entry, so this must precede any broader
        // pathmap whose key is a prefix of the generated-files directory. ExportUtil relocates
        // /generatedfilesout under output/, which the /pathmap:output/=<pdbDir> entry (emitted
        // when the PDB sits outside the pathmap root) would otherwise capture first.
        var pathmap = $"/pathmap:\"{absolute}{Path.DirectorySeparatorChar}={manifest.PathMapRoot}{generatedBase}/\"";
        var insertAt = lines.FindIndex(l => PathMapKeyIsPrefixOf(l, absolute));
        lines.Insert(insertAt >= 0 && insertAt < index ? insertAt : index, pathmap);
        File.WriteAllLines(rspPath, lines);
    }

    /// <summary>
    /// True when <paramref name="line"/> is a /pathmap entry whose (already-absolute) key is a
    /// prefix of <paramref name="path"/>, i.e. it would remap paths under that directory.
    /// </summary>
    private static bool PathMapKeyIsPrefixOf(string line, string path)
    {
        const string prefix = "/pathmap:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var mapping = line[prefix.Length..].Trim('"');
        var separator = mapping.IndexOf('=');
        return separator > 0 && path.StartsWith(mapping[..separator], StringComparison.Ordinal);
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

    /// <summary>
    /// Returns the directory's canonical path exactly as a child process's getcwd() will report
    /// it. Path.GetFullPath does not resolve symlinked intermediate components, so round-trip
    /// through the OS via the current directory instead.
    /// </summary>
    private static string CanonicalizeDirectory(string dir)
    {
        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(dir);
            return Directory.GetCurrentDirectory();
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    private static string? ReadPdbOption(string compilerArgsFile, string key)
    {
        var lines = File.ReadAllLines(compilerArgsFile);
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i] == key)
            {
                return lines[i + 1];
            }
        }
        return null;
    }

    private static bool IsExactCompiler(string? cscPath, string compilerVersion) =>
        cscPath != null &&
        string.Equals(CompilerVersionReader.TryGetInformationalVersion(cscPath), compilerVersion, StringComparison.OrdinalIgnoreCase);

    private static string GetDotnetRoot() =>
        Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/local/share/dotnet";

    /// <summary>
    /// Maps the PDB-recorded runtime informational version (e.g. "10.0.9-servicing.26270.113+sha")
    /// to an installed Microsoft.NETCore.App version usable with dotnet exec --fx-version
    /// (servicing/rtm builds install as the plain "10.0.9"; previews keep their prerelease label).
    /// </summary>
    private static string? FindInstalledRuntime(string? runtimeVersion)
    {
        if (string.IsNullOrEmpty(runtimeVersion))
        {
            return null;
        }

        var version = runtimeVersion.Split('+')[0];
        var runtimeDir = Path.Combine(GetDotnetRoot(), "shared", "Microsoft.NETCore.App");
        foreach (var candidate in new[] { version, version.Split('-')[0] })
        {
            if (Directory.Exists(Path.Combine(runtimeDir, candidate)))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindCsc(string? compilerVersion)
    {
        var sdkPath = Path.Combine(GetDotnetRoot(), "sdk");
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
        string cscPath, string? fxVersion, string exportDir, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = exportDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        if (fxVersion != null)
        {
            startInfo.ArgumentList.Add("--fx-version");
            startInfo.ArgumentList.Add(fxVersion);
        }
        startInfo.ArgumentList.Add(cscPath);
        startInfo.ArgumentList.Add("@build.rsp");

        using var process = Process.Start(startInfo)!;
        // Drain both streams concurrently: reading one to completion before the other can
        // deadlock if the child fills a pipe buffer on the stream we're not yet reading.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout + stderr);
    }
}
