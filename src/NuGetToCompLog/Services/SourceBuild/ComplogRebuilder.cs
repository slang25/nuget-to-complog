using System.Diagnostics;
using Basic.CompilerLog.Util;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.Reconstruction;

namespace NuGetToCompLog.Services.SourceBuild;

/// <summary>
/// The outcome of replaying a complog through csc. <see cref="RebuiltAssembly"/> is the path
/// csc was told to write, so it is only set when the compilation succeeded.
///
/// The two compiler versions are deliberately separate. <see cref="RequestedCompilerVersion"/> is
/// what the PDB says built the package; <see cref="ActualCompilerVersion"/> is what ran here. They
/// agree only when <see cref="CompilerWasExact"/> is true, and anything recording what produced
/// these bytes - a provenance file above all - has to say the second.
/// </summary>
public record RebuildOutcome(
    bool Success,
    string? Failure,
    string ExportDir,
    string? RebuiltAssembly,
    string? RebuiltPdb,
    string? CscPath,
    string? RequestedCompilerVersion,
    string? ActualCompilerVersion,
    bool CompilerWasExact,
    bool RuntimeWasExact)
{
    /// <summary>
    /// True when the rebuild ran on the exact compiler and runtime the original build used. Only
    /// then does a byte comparison against the shipped assembly mean anything: two compiler
    /// versions compiling identical source differ as a matter of course.
    /// </summary>
    public bool ToolchainWasExact => CompilerWasExact && RuntimeWasExact;
}

/// <summary>
/// Rebuilds an assembly from a complog: exports the log to a directory, repairs the response
/// file's path mappings for that directory, and runs the exact compiler the PDB recorded on the
/// runtime it recorded.
///
/// Everything after the export reads only the complog contents, which is what makes the result
/// meaningful - it is evidence the complog alone reproduces the assembly, not evidence that this
/// machine happens to hold the right files. Both <c>verify</c> (which compares the result) and
/// <c>sourcebuild</c> (which caches it) depend on that property, so the replay lives here rather
/// than in either command.
/// </summary>
public class ComplogRebuilder
{
    private readonly CscInvoker _csc;
    private readonly IConsoleWriter _console;

    public ComplogRebuilder(CscInvoker csc, IConsoleWriter console)
    {
        _csc = csc;
        _console = console;
    }

    /// <summary>
    /// Replays the compilation the complog holds for <paramref name="assemblyName"/>, writing the
    /// export under <paramref name="exportDir"/>. Records on the extraction's ledger which
    /// compiler and runtime actually ran, replacing the predictions made before one was chosen.
    /// </summary>
    public async Task<RebuildOutcome> RebuildAsync(
        PackageExtractionResult result,
        string complogPath,
        string assemblyName,
        string exportDir,
        bool fetchCompiler,
        CancellationToken cancellationToken = default)
    {
        var compilerVersion = ReadPdbOption(result.CompilerArgsFile!, "compiler-version");
        // The PDB's compilation options record the runtime that hosted the original compiler, and
        // it lands in the options blob, so it is selected alongside the compiler rather than after.
        var runtimeVersion = ReadPdbOption(result.CompilerArgsFile!, "runtime-version");
        var compiler = await _csc.SelectAsync(compilerVersion, runtimeVersion, fetchCompiler, cancellationToken);
        if (compiler == null)
        {
            return Failed(exportDir, "No csc.dll found in installed SDKs");
        }
        var cscPath = compiler.CscPath;

        // Export the complog to a build-able directory. Everything from here on uses only the
        // complog contents - this is what proves the complog alone reproduces the assembly.
        using (var reader = CompilerLogReader.Create(complogPath))
        {
            var compilerCalls = reader.ReadAllCompilerCalls();
            var compilerCall = compilerCalls.FirstOrDefault(c =>
                string.Equals(Path.GetFileNameWithoutExtension(c.ProjectFileName), assemblyName,
                    StringComparison.OrdinalIgnoreCase));
            if (compilerCall == null)
            {
                return Failed(exportDir,
                    $"The complog holds no compilation for {assemblyName} " +
                    $"(found: {string.Join(", ", compilerCalls.Select(c => Path.GetFileNameWithoutExtension(c.ProjectFileName)))})");
            }

            var compilerDir = Path.GetDirectoryName(cscPath)!;
            new ExportUtil(reader).Export(compilerCall, exportDir, [(compilerDir, "rebuild")]);
        }

        // csc resolves the rsp's relative source paths against its working directory as the OS
        // reports it, with symlinks resolved (macOS: /var -> /private/var, /tmp -> /private/tmp).
        // Pathmap keys must be built from that canonical form or they never match and every
        // document keeps its machine-local absolute path.
        exportDir = CanonicalizeDirectory(exportDir);

        var rspPath = Path.Combine(exportDir, "build.rsp");
        if (!File.Exists(rspPath))
        {
            return Failed(exportDir, "Export did not produce build.rsp");
        }

        MakePathMapKeysAbsolute(rspPath, exportDir);
        MapGeneratedFilesOut(rspPath, exportDir, result.WorkingDirectory);
        EnsureOutputDirectories(rspPath, exportDir);

        _console.WriteLine();
        _console.MarkupLine("[yellow]Rebuilding from complog[/]");
        _console.MarkupLine($"  [dim]Compiler: {cscPath}[/]");

        // The complog was built before a compiler was chosen, so its ledger entry was a
        // prediction; this is what the rebuild actually ran.
        if (compilerVersion != null)
        {
            if (!compiler.CompilerWasExact)
            {
                var actual = CompilerVersionReader.TryGetInformationalVersion(cscPath);
                _console.MarkupLine($"  [yellow]⚠[/] Exact compiler {compilerVersion.Split('+')[0]} is not installed; " +
                                    $"using {actual?.Split('+')[0]} - a byte-for-byte match is unlikely");
                result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, compilerVersion.Split('+')[0],
                    InputEvidence.Assumed,
                    $"rebuilt with {actual?.Split('+')[0]} instead - codegen and generated-document " +
                    "checksums differ between compiler versions");
            }
            else
            {
                result.Ledger.Replace(ReconstructionLedger.CategoryCompiler, compilerVersion.Split('+')[0],
                    InputEvidence.Proven,
                    "the exact compiler recorded in the PDB, verified by its informational version");
            }
        }

        if (runtimeVersion != null && !compiler.RuntimeWasExact)
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

        var (exitCode, output) = await CscInvoker.RunAsync(
            compiler with { CscPath = cscPath }, exportDir, "build.rsp", cancellationToken);
        if (exitCode != 0)
        {
            _console.MarkupLine($"[red]✗[/] Rebuild failed (csc exit code {exitCode}):");
            foreach (var line in output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                _console.MarkupLine($"  [dim]{line.Trim().Replace("[", "[[").Replace("]", "]]")}[/]");
            }
            return Failed(exportDir, $"csc exited with code {exitCode}");
        }

        var rspLines = File.ReadAllLines(rspPath);
        var rebuiltDll = ResolveRspPath(rspLines, "/out:", exportDir);
        if (rebuiltDll == null || !File.Exists(rebuiltDll))
        {
            return Failed(exportDir, "Rebuild produced no output assembly");
        }

        return new RebuildOutcome(
            true, null, exportDir, rebuiltDll,
            ResolveRspPath(rspLines, "/pdb:", exportDir),
            cscPath, compilerVersion, CompilerVersionReader.TryGetInformationalVersion(cscPath),
            compiler.CompilerWasExact, compiler.RuntimeWasExact);
    }

    private static RebuildOutcome Failed(string exportDir, string reason) =>
        new(false, reason, exportDir, null, null, null, null, null, false, false);

    public static string? ResolveRspPath(string[] rspLines, string prefix, string exportDir)
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

    public static string? ReadPdbOption(string compilerArgsFile, string key)
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





}
