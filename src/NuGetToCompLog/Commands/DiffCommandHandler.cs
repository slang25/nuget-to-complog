using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services.Patch;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the diff command: generates a unified diff patch from user edits.
/// </summary>
public class DiffCommandHandler
{
    private readonly PatchManager _patchManager;
    private readonly UnifiedDiffGenerator _diffGenerator;
    private readonly IConsoleWriter _console;

    public DiffCommandHandler(
        PatchManager patchManager,
        UnifiedDiffGenerator diffGenerator,
        IConsoleWriter console)
    {
        _patchManager = patchManager;
        _diffGenerator = diffGenerator;
        _console = console;
    }

    public async Task<string?> HandleAsync(
        string packageId,
        string? version = null,
        string? patchesDir = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _console.WritePanel(
                "Creating Patch",
                $"[cyan]{packageId}[/] {(version != null ? $"[yellow]{version}[/]" : "")}",
                "Yellow");

            var patchDir = _patchManager.FindPatchDirectory(packageId, version, patchesDir);
            if (patchDir == null)
            {
                _console.MarkupLine($"[red]\u2717[/] No ejected package found for [cyan]{packageId}[/]");
                _console.MarkupLine("[dim]   Run 'eject' first to create an editable project[/]");
                return null;
            }

            var metadata = await _patchManager.ReadPatchMetadataAsync(patchDir);
            if (metadata == null)
            {
                _console.MarkupLine("[red]\u2717[/] Invalid patch directory - missing patch-metadata.json");
                return null;
            }

            var originalDir = Path.Combine(patchDir, ".original");
            var srcDir = Path.Combine(patchDir, "src");

            if (!Directory.Exists(originalDir) || !Directory.Exists(srcDir))
            {
                _console.MarkupLine("[red]\u2717[/] Invalid patch directory - missing .original/ or src/");
                return null;
            }

            _console.MarkupLine("[dim]Comparing .original/ with src/...[/]");
            var diffResult = _diffGenerator.GenerateDiff(originalDir, srcDir);

            if (!diffResult.HasChanges)
            {
                _console.MarkupLine("[yellow]\u26a0[/] No changes detected");
                _console.MarkupLine("[dim]   Edit files in src/ to create a patch[/]");
                return null;
            }

            // Report changes
            _console.WriteLine();
            if (diffResult.ModifiedFiles.Count > 0)
            {
                foreach (var file in diffResult.ModifiedFiles)
                {
                    _console.MarkupLine($"  [yellow]M[/] {file}");
                }
            }
            if (diffResult.AddedFiles.Count > 0)
            {
                foreach (var file in diffResult.AddedFiles)
                {
                    _console.MarkupLine($"  [green]A[/] {file}");
                }
            }
            if (diffResult.DeletedFiles.Count > 0)
            {
                foreach (var file in diffResult.DeletedFiles)
                {
                    _console.MarkupLine($"  [red]D[/] {file}");
                }
            }

            // Write patch file
            var patchContent = _diffGenerator.FormatDiff(diffResult);
            var patchFileName = $"{metadata.PackageId}+{metadata.Version}.patch";
            var patchesBaseDir = Path.GetDirectoryName(patchDir)!;
            var patchFilePath = Path.Combine(patchesBaseDir, patchFileName);

            await File.WriteAllTextAsync(patchFilePath, patchContent, cancellationToken);

            _console.WriteLine();
            _console.WritePanel(
                "Patch Created",
                $"[green]\u2713[/] {diffResult.TotalChangedFiles} file(s) changed\n" +
                $"  [cyan]{patchFilePath}[/]\n\n" +
                $"[dim]Run[/] [yellow]n2cl apply[/] [dim]to rebuild with this patch[/]",
                "Green");

            return patchFilePath;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return null;
        }
    }
}
