using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services.Patch;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the apply command: applies patches and rebuilds assemblies.
/// </summary>
public class ApplyCommandHandler
{
    private readonly PatchManager _patchManager;
    private readonly PatchApplier _patchApplier;
    private readonly AssemblyRebuilder _rebuilder;
    private readonly IConsoleWriter _console;

    public ApplyCommandHandler(
        PatchManager patchManager,
        PatchApplier patchApplier,
        AssemblyRebuilder rebuilder,
        IConsoleWriter console)
    {
        _patchManager = patchManager;
        _patchApplier = patchApplier;
        _rebuilder = rebuilder;
        _console = console;
    }

    public async Task<bool> HandleAsync(
        string? packageId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _console.WritePanel(
                "Applying Patches",
                packageId != null
                    ? $"[cyan]{packageId}[/]"
                    : "[dim]All patches[/]",
                "Cyan1");

            // Find patch files to apply
            var patchFiles = _patchManager.ListPatchFiles();
            if (packageId != null)
            {
                patchFiles = patchFiles
                    .Where(f => Path.GetFileName(f).StartsWith($"{packageId}+", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (patchFiles.Count == 0)
            {
                // No .patch files - check if there are ejected packages with modifications
                var ejectedDirs = _patchManager.ListEjectedPackages();
                if (packageId != null)
                {
                    ejectedDirs = ejectedDirs
                        .Where(d => Path.GetFileName(d).StartsWith($"{packageId}+", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                if (ejectedDirs.Count == 0)
                {
                    _console.MarkupLine("[yellow]\u26a0[/] No patches or ejected packages found");
                    _console.MarkupLine("[dim]   Run 'eject' to create an editable project, or 'diff' to create a patch[/]");
                    return false;
                }

                // Build directly from ejected source (no patch file needed)
                return await BuildFromEjectedSourcesAsync(ejectedDirs, cancellationToken);
            }

            return await ApplyPatchFilesAsync(patchFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return false;
        }
    }

    private async Task<bool> BuildFromEjectedSourcesAsync(List<string> ejectedDirs, CancellationToken cancellationToken)
    {
        var allSuccess = true;

        foreach (var patchDir in ejectedDirs)
        {
            var metadata = await _patchManager.ReadPatchMetadataAsync(patchDir);
            if (metadata == null)
            {
                _console.MarkupLine($"[yellow]\u26a0[/] Skipping {Path.GetFileName(patchDir)} - invalid metadata");
                continue;
            }

            _console.MarkupLine($"\n[cyan]\u2192[/] Building [yellow]{metadata.PackageId} {metadata.Version}[/] from ejected source...");

            // Build directly using the src/ directory (which the user has edited)
            var result = await _rebuilder.RebuildAsync(patchDir, cancellationToken: cancellationToken);

            if (result.Success)
            {
                _console.MarkupLine($"  [green]\u2713[/] Built successfully: [dim]{result.OutputAssemblyPath}[/]");
            }
            else
            {
                _console.MarkupLine($"  [red]\u2717[/] Build failed:");
                foreach (var line in result.Output.Split('\n').Take(20))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _console.MarkupLine($"    [dim]{EscapeMarkup(line)}[/]");
                    }
                }
                allSuccess = false;
            }
        }

        PrintSummary(allSuccess);
        return allSuccess;
    }

    private async Task<bool> ApplyPatchFilesAsync(List<string> patchFiles, CancellationToken cancellationToken)
    {
        var allSuccess = true;

        foreach (var patchFile in patchFiles)
        {
            var fileName = Path.GetFileName(patchFile);
            _console.MarkupLine($"\n[cyan]\u2192[/] Applying [yellow]{fileName}[/]...");

            // Parse package identity from filename (PackageId+Version.patch)
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var plusIndex = baseName.LastIndexOf('+');
            if (plusIndex < 0)
            {
                _console.MarkupLine($"  [yellow]\u26a0[/] Skipping - invalid patch filename format");
                continue;
            }

            var pkgId = baseName[..plusIndex];
            var pkgVersion = baseName[(plusIndex + 1)..];

            // Find the corresponding ejected directory
            var patchDir = _patchManager.FindPatchDirectory(pkgId, pkgVersion);
            if (patchDir == null)
            {
                _console.MarkupLine($"  [red]\u2717[/] No ejected package found for {pkgId}+{pkgVersion}");
                allSuccess = false;
                continue;
            }

            // Apply patch to .original/ source into a temp patched directory
            var patchContent = await File.ReadAllTextAsync(patchFile, cancellationToken);
            var patchedDir = Path.Combine(patchDir, ".patched");

            // Clean previous patched dir
            if (Directory.Exists(patchedDir))
            {
                Directory.Delete(patchedDir, recursive: true);
            }

            var originalDir = Path.Combine(patchDir, ".original");
            var applyResult = _patchApplier.Apply(patchContent, originalDir, patchedDir);

            if (!applyResult.Success)
            {
                _console.MarkupLine($"  [red]\u2717[/] Patch application failed:");
                foreach (var (file, error) in applyResult.FailedFiles)
                {
                    _console.MarkupLine($"    [red]{file}[/]: {EscapeMarkup(error)}");
                }
                allSuccess = false;
                continue;
            }

            _console.MarkupLine($"  [green]\u2713[/] Patch applied to {applyResult.AppliedFiles.Count} file(s)");

            // Copy unmodified files from .original/ to .patched/ so we have a complete source tree
            // (excluding files that the patch explicitly deletes)
            CopyUnpatchedFiles(originalDir, patchedDir, applyResult.DeletedFiles);

            // Rebuild with patched sources
            var rebuildResult = await _rebuilder.RebuildAsync(patchDir, patchedDir, cancellationToken);

            if (rebuildResult.Success)
            {
                _console.MarkupLine($"  [green]\u2713[/] Built successfully: [dim]{rebuildResult.OutputAssemblyPath}[/]");
            }
            else
            {
                _console.MarkupLine($"  [red]\u2717[/] Build failed:");
                foreach (var line in rebuildResult.Output.Split('\n').Take(20))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _console.MarkupLine($"    [dim]{EscapeMarkup(line)}[/]");
                    }
                }
                allSuccess = false;
            }
        }

        PrintSummary(allSuccess);
        return allSuccess;
    }

    private void CopyUnpatchedFiles(string originalDir, string patchedDir, List<string> deletedFiles)
    {
        if (!Directory.Exists(originalDir))
            return;

        var deletedSet = new HashSet<string>(deletedFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(originalDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(originalDir, file);

            // Skip files that were explicitly deleted by the patch
            if (deletedSet.Contains(relativePath))
                continue;

            var destPath = Path.Combine(patchedDir, relativePath);

            if (!File.Exists(destPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath);
            }
        }
    }

    private void PrintSummary(bool allSuccess)
    {
        _console.WriteLine();
        if (allSuccess)
        {
            _console.WritePanel(
                "Apply Complete",
                "[green]\u2713 All patches applied and assemblies rebuilt successfully[/]",
                "Green");
        }
        else
        {
            _console.WritePanel(
                "Apply Completed with Errors",
                "[yellow]\u26a0 Some patches failed to apply or build[/]\n[dim]Check the output above for details[/]",
                "Yellow");
        }
    }

    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
