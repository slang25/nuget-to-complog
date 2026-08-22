using System.Xml.Linq;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.Patch;
using NuGetToCompLog.Services.Swap;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the swap command: ejects a NuGet package into an editable source project with a
/// generated .csproj, then rewrites the consuming project's PackageReference into a
/// ProjectReference to it. From then on plain `dotnet build` compiles the recovered source,
/// so the package can be edited like any project in the solution.
/// </summary>
public class SwapCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly ProjectGenerator _projectGenerator;
    private readonly MsBuildProjectGenerator _msbuildProjectGenerator;
    private readonly IConsoleWriter _console;

    public SwapCommandHandler(
        PackageAnalysisPipeline pipeline,
        ProjectGenerator projectGenerator,
        MsBuildProjectGenerator msbuildProjectGenerator,
        IConsoleWriter console)
    {
        _pipeline = pipeline;
        _projectGenerator = projectGenerator;
        _msbuildProjectGenerator = msbuildProjectGenerator;
        _console = console;
    }

    public async Task<string?> HandleAsync(
        string packageId,
        string? version = null,
        string? project = null,
        string? outputDirectory = null,
        string? assembly = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var workingDirectory = Directory.GetCurrentDirectory();
            var projectPath = PackageReferenceSwapper.FindProjectFile(project, workingDirectory);

            var projectDoc = XDocument.Load(projectPath);
            var packageReference = PackageReferenceSwapper.FindPackageReference(projectDoc, packageId);
            if (packageReference == null)
            {
                _console.MarkupLine($"[red]✗[/] No PackageReference to [cyan]{packageId}[/] in [dim]{projectPath}[/]");
                var existing = PackageReferenceSwapper.ListPackageReferences(projectDoc);
                if (existing.Count > 0)
                {
                    _console.MarkupLine($"[dim]   Package references in this project: {string.Join(", ", existing)}[/]");
                }
                return null;
            }

            version ??= PackageReferenceSwapper.ResolveVersion(projectPath, packageReference, packageId);
            if (version == null)
            {
                _console.MarkupLine($"[red]✗[/] Could not determine the version of [cyan]{packageId}[/] " +
                    "from the project or Directory.Packages.props");
                _console.MarkupLine("[dim]   Pass the version explicitly: swap <packageId> <version>[/]");
                return null;
            }

            _console.WritePanel(
                "Swapping Package Reference",
                $"[cyan]{packageId}[/] [yellow]{version}[/]\n" +
                $"[dim]in {Path.GetFileName(projectPath)}[/]",
                "Green");

            var result = await _pipeline.AnalyzeAsync(packageId, version, assembly, cancellationToken);
            if (result == null)
            {
                _console.MarkupLine("[red]✗[/] Failed to analyze package");
                return null;
            }

            if (result.CompilerArgsFile == null)
            {
                _console.MarkupLine("[red]✗[/] Cannot swap - no compiler arguments found in PDB");
                _console.MarkupLine("[dim]   The package needs embedded PDBs or symbol packages with compiler arguments[/]");
                return null;
            }

            var sourcesExist = Directory.Exists(result.SourcesDirectory) &&
                               Directory.GetFiles(result.SourcesDirectory, "*", SearchOption.AllDirectories).Length > 0;
            if (!sourcesExist)
            {
                _console.MarkupLine("[red]✗[/] Cannot swap - no source files recovered");
                _console.MarkupLine("[dim]   The package needs Source Link or embedded sources[/]");
                return null;
            }

            _console.WriteLine();
            _console.MarkupLine("[yellow]Generating editable project...[/]");

            var patchesDir = outputDirectory ?? Path.Combine(workingDirectory, "patches");
            var patchDir = await _projectGenerator.GenerateAsync(result, patchesDir);
            var csprojPath = await _msbuildProjectGenerator.GenerateAsync(result, patchDir);

            var relativeCsprojPath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, csprojPath);
            PackageReferenceSwapper.Swap(projectPath, packageId, relativeCsprojPath);
            _console.MarkupLine($"  [green]✓[/] Replaced PackageReference with ProjectReference in {Path.GetFileName(projectPath)}");

            _console.WriteLine();
            _console.WritePanel(
                "Swap Complete",
                $"[green]{packageId} {version} is now built from source:[/]\n" +
                $"  [cyan]{patchDir}[/]\n\n" +
                $"[dim]Edit files in src/, then build your project as usual:[/]\n" +
                $"  [yellow]dotnet build[/]\n\n" +
                $"[dim]To capture your edits as a committable patch:[/]\n" +
                $"  [yellow]nuget-to-complog diff {packageId}[/]\n\n" +
                $"[dim]To undo the swap, revert {Path.GetFileName(projectPath)} (e.g. git checkout)[/]",
                "Green");

            return patchDir;
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]✗[/] {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return null;
        }
    }
}
