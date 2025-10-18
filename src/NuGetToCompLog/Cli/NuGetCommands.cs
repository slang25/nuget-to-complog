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
    private readonly ProcessPackageCommandHandler _handler;
    private readonly IConsoleWriter _console;

    public NuGetCommands(ProcessPackageCommandHandler handler, IConsoleWriter console)
    {
        _handler = handler;
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
            var result = await _handler.HandleAsync(command);

            if (result == null)
            {
                throw new Exception("Failed to process package");
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }
}
