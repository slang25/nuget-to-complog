using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.Patch;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the eject command: extracts a NuGet package into an editable project.
/// </summary>
public class EjectPackageCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly ProjectGenerator _projectGenerator;
    private readonly IConsoleWriter _console;

    public EjectPackageCommandHandler(
        PackageAnalysisPipeline pipeline,
        ProjectGenerator projectGenerator,
        IConsoleWriter console)
    {
        _pipeline = pipeline;
        _projectGenerator = projectGenerator;
        _console = console;
    }

    public async Task<string?> HandleAsync(
        string packageId,
        string? version = null,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _console.WritePanel(
                "Ejecting Package",
                $"[cyan]{packageId}[/] {(version != null ? $"[yellow]{version}[/]" : "[dim](latest)[/]")}",
                "Green");

            var result = await _pipeline.AnalyzeAsync(packageId, version, cancellationToken);
            if (result == null)
            {
                _console.MarkupLine("[red]\u2717[/] Failed to analyze package");
                return null;
            }

            if (result.CompilerArgsFile == null)
            {
                _console.MarkupLine("[red]\u2717[/] Cannot eject - no compiler arguments found in PDB");
                _console.MarkupLine("[dim]   The package needs embedded PDBs or symbol packages with compiler arguments[/]");
                return null;
            }

            var sourcesExist = Directory.Exists(result.SourcesDirectory) &&
                               Directory.GetFiles(result.SourcesDirectory, "*", SearchOption.AllDirectories).Length > 0;
            if (!sourcesExist)
            {
                _console.MarkupLine("[red]\u2717[/] Cannot eject - no source files recovered");
                _console.MarkupLine("[dim]   The package needs Source Link or embedded sources[/]");
                return null;
            }

            _console.WriteLine();
            _console.MarkupLine("[yellow]Generating editable project...[/]");

            var patchesDir = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "patches");
            var patchDir = await _projectGenerator.GenerateAsync(result, patchesDir);

            _console.WriteLine();
            _console.WritePanel(
                "Eject Complete",
                $"[green]Package ejected to:[/]\n" +
                $"  [cyan]{patchDir}[/]\n\n" +
                $"[dim]Edit source files in src/, then run:[/]\n" +
                $"  [yellow]n2cl diff {packageId}[/]    [dim]- to create a patch[/]\n" +
                $"  [yellow]n2cl apply[/]              [dim]- to rebuild with patches[/]",
                "Green");

            return patchDir;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return null;
        }
    }
}
