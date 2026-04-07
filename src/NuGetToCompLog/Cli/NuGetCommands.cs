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
    private readonly IConsoleWriter _console;

    public NuGetCommands(
        ProcessPackageCommandHandler processHandler,
        EjectPackageCommandHandler ejectHandler,
        DiffCommandHandler diffHandler,
        ApplyCommandHandler applyHandler,
        IConsoleWriter console)
    {
        _processHandler = processHandler;
        _ejectHandler = ejectHandler;
        _diffHandler = diffHandler;
        _applyHandler = applyHandler;
        _console = console;
    }

    /// <summary>
    /// Process a NuGet package and create a CompLog file.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Newtonsoft.Json)</param>
    /// <param name="version">The package version (e.g., 13.0.3). If not specified, uses latest stable version.</param>
    [Command("")]
    public async Task Process(
        [Argument] string packageId,
        [Argument] string? version = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var command = new ProcessPackageCommand(packageId, version);
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
    /// Create a patch from changes made to an ejected package.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier</param>
    /// <param name="version">The package version (auto-detected if only one version is ejected)</param>
    [Command("diff")]
    public async Task Diff(
        [Argument] string packageId,
        [Argument] string? version = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _diffHandler.HandleAsync(packageId, version);

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
    [Command("apply")]
    public async Task Apply(
        [Argument] string? packageId = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _applyHandler.HandleAsync(packageId);

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
    [Command("eject")]
    public async Task Eject(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? output = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _ejectHandler.HandleAsync(packageId, version, output);

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
