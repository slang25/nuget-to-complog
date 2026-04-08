using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.CompLog;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the ProcessPackageCommand by orchestrating the analysis pipeline
/// and complog creation.
/// </summary>
public class ProcessPackageCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly CompLogStructureCreator _structureCreator;
    private readonly IConsoleWriter _console;

    public ProcessPackageCommandHandler(
        PackageAnalysisPipeline pipeline,
        CompLogStructureCreator structureCreator,
        IConsoleWriter console)
    {
        _pipeline = pipeline;
        _structureCreator = structureCreator;
        _console = console;
    }

    public async Task<string?> HandleAsync(ProcessPackageCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            DisplayHeader(command.PackageId, command.Version);

            var result = await _pipeline.AnalyzeAsync(command.PackageId, command.Version, cancellationToken);
            if (result == null)
            {
                return null;
            }

            // Check if compiler arguments were extracted
            if (result.CompilerArgsFile == null)
            {
                _console.MarkupLine("[red]\u2717[/] Cannot create complog - no compiler arguments found");
                _console.MarkupLine("[yellow]\u26a0[/] This package does not contain PDBs with compiler arguments");
                _console.MarkupLine("[dim]   Packages need embedded PDBs or symbol packages (.snupkg) to extract compiler arguments[/]");
                return null;
            }

            var complogStructurePath = _structureCreator.CreateStructure(
                command.PackageId,
                result.Package.Version,
                result.WorkingDirectory,
                null,
                result.SelectedAssemblies);

            var complogFilePath = await CompLogFileCreator.CreateCompLogFileAsync(
                command.PackageId,
                result.Package.Version,
                result.WorkingDirectory,
                Directory.GetCurrentDirectory(),
                result.SelectedTfm,
                result.SelectedAssemblies);

            if (!File.Exists(complogFilePath))
            {
                _console.MarkupLine("[red]\u2717[/] CompLog file was not created - check that the package has embedded PDBs with compiler arguments");
                return null;
            }

            return complogFilePath;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return null;
        }
    }

    private void DisplayHeader(string packageId, string? version)
    {
        _console.WritePanel(
            "Processing Package",
            $"[cyan]{packageId}[/] {(version != null ? $"[yellow]{version}[/]" : "[dim](latest)[/]")}",
            "Cyan1");
    }
}
