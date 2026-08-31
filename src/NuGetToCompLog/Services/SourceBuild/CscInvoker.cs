using System.Diagnostics;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.SourceBuild;

/// <summary>Which compiler and runtime a rebuild actually got, against what it asked for.</summary>
public record CompilerSelection(string CscPath, string? FxVersion, bool CompilerWasExact, bool RuntimeWasExact);

/// <summary>
/// Finds the compiler a package was built with and runs it.
///
/// Both things matter and for the same reason. Roslyn versions differ in codegen and in the
/// checksums they give generated documents, and the PDB's compilation-options blob records the
/// runtime that hosted the compiler - so a rebuild on the recorded pair can match the shipped
/// assembly byte for byte, and a rebuild on anything else cannot. Every path that rebuilds an
/// assembly wants that, which is why this is one place rather than one per command.
/// </summary>
public class CscInvoker
{
    private readonly CompilerToolsetService _toolset;
    private readonly IConsoleWriter _console;

    public CscInvoker(CompilerToolsetService toolset, IConsoleWriter console)
    {
        _toolset = toolset;
        _console = console;
    }

    /// <summary>
    /// Resolves the compiler for <paramref name="compilerVersion"/> - the installed SDK that has
    /// it, else the newest installed one - and the runtime for <paramref name="runtimeVersion"/>.
    /// With <paramref name="fetchCompiler"/> the exact compiler is downloaded as the matching
    /// Microsoft.Net.Compilers.Toolset package rather than settling for a local one. Returns null
    /// when no csc could be found at all.
    /// </summary>
    public async Task<CompilerSelection?> SelectAsync(
        string? compilerVersion,
        string? runtimeVersion,
        bool fetchCompiler,
        CancellationToken cancellationToken = default)
    {
        var cscPath = FindCsc(compilerVersion);

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
            return null;
        }

        var fxVersion = FindInstalledRuntime(runtimeVersion);
        return new CompilerSelection(
            cscPath,
            fxVersion,
            CompilerWasExact: compilerVersion == null || IsExactCompiler(cscPath, compilerVersion),
            RuntimeWasExact: runtimeVersion == null || fxVersion != null);
    }

    /// <summary>Runs csc against a response file in <paramref name="workingDirectory"/>.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        CompilerSelection compiler,
        string workingDirectory,
        string responseFileName,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        if (compiler.FxVersion != null)
        {
            startInfo.ArgumentList.Add("--fx-version");
            startInfo.ArgumentList.Add(compiler.FxVersion);
        }
        startInfo.ArgumentList.Add(compiler.CscPath);
        startInfo.ArgumentList.Add($"@{responseFileName}");

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

    public static bool IsExactCompiler(string? cscPath, string compilerVersion) =>
        cscPath != null &&
        string.Equals(CompilerVersionReader.TryGetInformationalVersion(cscPath), compilerVersion,
            StringComparison.OrdinalIgnoreCase);

    private static string GetDotnetRoot() =>
        Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/local/share/dotnet";

    /// <summary>
    /// Maps the PDB-recorded runtime informational version (e.g. "10.0.9-servicing.26270.113+sha")
    /// to an installed Microsoft.NETCore.App version usable with dotnet exec --fx-version
    /// (servicing/rtm builds install as the plain "10.0.9"; previews keep their prerelease label).
    /// </summary>
    public static string? FindInstalledRuntime(string? runtimeVersion)
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

    public static string? FindCsc(string? compilerVersion)
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
            var exact = candidates.FirstOrDefault(c => IsExactCompiler(c, compilerVersion));
            if (exact != null)
            {
                return exact;
            }
        }

        return candidates.FirstOrDefault();
    }
}
