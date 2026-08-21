using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Commands;

namespace NuGetToCompLog.Cli;

/// <summary>
/// CLI commands for NuGet to CompLog tool.
/// Commands are automatically discovered and wired up by ConsoleAppFramework.
/// </summary>
public class NuGetCommands
{
    private readonly ProcessPackageCommandHandler _processHandler;
    private readonly EjectPackageCommandHandler _ejectHandler;
    private readonly DiffCommandHandler _diffHandler;
    private readonly ApplyCommandHandler _applyHandler;
    private readonly VerifyCommandHandler _verifyHandler;
    private readonly IConsoleWriter _console;

    /// <summary>
    /// Whether to keep package-controlled generator code out of this process. Proving a
    /// generator regenerates the recorded documents means running it here, so the switch also
    /// exists as an environment variable for unattended runs that never see a command line.
    /// </summary>
    private static bool SkipGenerators(bool flag) =>
        flag || Environment.GetEnvironmentVariable("NUGET_TO_COMPLOG_SKIP_GENERATORS") is "1" or "true";

    public NuGetCommands(
        ProcessPackageCommandHandler processHandler,
        EjectPackageCommandHandler ejectHandler,
        DiffCommandHandler diffHandler,
        ApplyCommandHandler applyHandler,
        VerifyCommandHandler verifyHandler,
        IConsoleWriter console)
    {
        _processHandler = processHandler;
        _ejectHandler = ejectHandler;
        _diffHandler = diffHandler;
        _applyHandler = applyHandler;
        _verifyHandler = verifyHandler;
        _console = console;
    }

    /// <summary>
    /// Process a NuGet package and create a CompLog file.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Newtonsoft.Json)</param>
    /// <param name="version">The package version (e.g., 13.0.3). If not specified, uses latest stable version.</param>
    /// <param name="assembly">Which assembly to capture when the package ships several for one target framework (e.g. nunit.framework.dll). Defaults to the one named after the package.</param>
    /// <param name="skipGenerators">Do not load or run source generator assemblies in this process. Their documents are then passed as plain source files, which cannot reproduce the original PDB exactly.</param>
    [Command("")]
    public async Task Process(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? assembly = null,
        bool skipGenerators = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var command = new ProcessPackageCommand(
                packageId, version, assembly, RunGenerators: !SkipGenerators(skipGenerators));
            var result = await _processHandler.HandleAsync(command);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Verify a package round-trips: create a complog, rebuild from it with the exact compiler,
    /// and byte-compare the result against the assembly shipped in the package.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Serilog)</param>
    /// <param name="version">The package version. If not specified, uses latest stable version.</param>
    /// <param name="fetchCompiler">Download the exact compiler (Microsoft.Net.Compilers.Toolset) from nuget.org or the dnceng dotnet-tools feed when it is not installed locally.</param>
    /// <param name="assembly">Which assembly to verify when the package ships several for one target framework (e.g. nunit.framework.legacy.dll). Defaults to the one named after the package.</param>
    /// <param name="skipGenerators">Do not load or run source generator assemblies in this process. Their documents are then passed as plain source files, which cannot reproduce the original PDB exactly.</param>
    [Command("verify")]
    public async Task Verify(
        [Argument] string packageId,
        [Argument] string? version = null,
        bool fetchCompiler = false,
        string? assembly = null,
        bool skipGenerators = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            Environment.ExitCode = await _verifyHandler.HandleAsync(
                packageId, version, fetchCompiler, assembly, runGenerators: !SkipGenerators(skipGenerators));
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Create a patch from changes made to an ejected package.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier</param>
    /// <param name="version">The package version (auto-detected if only one version is ejected)</param>
    /// <param name="patchesDir">Base directory for patches. Defaults to ./patches/</param>
    [Command("diff")]
    public async Task Diff(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? patchesDir = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _diffHandler.HandleAsync(packageId, version, patchesDir);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Apply patches and rebuild assemblies.
    /// </summary>
    /// <param name="packageId">Optional: apply only patches for this package</param>
    /// <param name="patchesDir">Base directory for patches. Defaults to ./patches/</param>
    [Command("apply")]
    public async Task Apply(
        [Argument] string? packageId = null,
        string? patchesDir = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _applyHandler.HandleAsync(packageId, patchesDir);

            if (!result)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Eject a NuGet package into an editable source project.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Newtonsoft.Json)</param>
    /// <param name="version">The package version. If not specified, uses latest stable version.</param>
    /// <param name="output">Output directory for patches. Defaults to ./patches/</param>
    /// <param name="assembly">Which assembly to eject when the package ships several for one target framework. Defaults to the one named after the package.</param>
    [Command("eject")]
    public async Task Eject(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? output = null,
        string? assembly = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _ejectHandler.HandleAsync(packageId, version, output, assembly);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }
}
