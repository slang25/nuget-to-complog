namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Parses and applies unified diff patches to source files.
/// </summary>
public class PatchApplier
{
    /// <summary>
    /// Applies a unified diff patch file to a source directory.
    /// Creates patched files in the output directory.
    /// </summary>
    public PatchApplicationResult Apply(string patchContent, string sourceDir, string outputDir)
    {
        var result = new PatchApplicationResult();
        var filePatches = ParsePatch(patchContent);

        foreach (var filePatch in filePatches)
        {
            try
            {
                if (filePatch.IsDelete)
                {
                    // Deleted files: don't create output, track for exclusion from copy
                    result.DeletedFiles.Add(filePatch.OriginalPath);
                    result.AppliedFiles.Add(filePatch.OriginalPath);
                }
                else
                {
                    ApplyFilePatch(filePatch, sourceDir, outputDir);
                    result.AppliedFiles.Add(filePatch.Path);
                }
            }
            catch (Exception ex)
            {
                result.FailedFiles.Add((filePatch.Path, ex.Message));
            }
        }

        return result;
    }

    private List<FilePatch> ParsePatch(string patchContent)
    {
        var patches = new List<FilePatch>();
        // Normalize CRLF to LF before splitting
        var lines = patchContent.Replace("\r\n", "\n").Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            // Look for file headers
            if (i < lines.Length - 1 && lines[i].StartsWith("--- ") && lines[i + 1].StartsWith("+++ "))
            {
                var originalHeader = lines[i][4..];
                var modifiedHeader = lines[i + 1][4..];

                // Parse paths, handling /dev/null for adds/deletes
                var isAdd = originalHeader == "/dev/null";
                var isDelete = modifiedHeader == "/dev/null";

                var originalPath = isAdd ? "" : (originalHeader.StartsWith("a/") ? originalHeader[2..] : originalHeader);
                var modifiedPath = isDelete ? "" : (modifiedHeader.StartsWith("b/") ? modifiedHeader[2..] : modifiedHeader);

                i += 2;

                var hunks = new List<PatchHunk>();

                // Parse hunks
                while (i < lines.Length && lines[i].StartsWith("@@"))
                {
                    var hunk = ParseHunk(lines, ref i);
                    if (hunk != null)
                    {
                        hunks.Add(hunk);
                    }
                }

                patches.Add(new FilePatch(
                    isDelete ? originalPath : modifiedPath,
                    isAdd ? modifiedPath : originalPath,
                    hunks,
                    isDelete));
            }
            else
            {
                i++;
            }
        }

        return patches;
    }

    private PatchHunk? ParseHunk(string[] lines, ref int i)
    {
        // Parse @@ -origStart,origCount +modStart,modCount @@
        var header = lines[i];
        var parts = header.Split("@@", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
        {
            i++;
            return null;
        }

        var range = parts[0].Trim();
        var rangeParts = range.Split(' ');
        if (rangeParts.Length < 2)
        {
            i++;
            return null;
        }

        var origRange = ParseRange(rangeParts[0][1..]); // skip '-'
        var modRange = ParseRange(rangeParts[1][1..]);   // skip '+'

        i++; // skip header line

        var hunkLines = new List<string>();
        while (i < lines.Length && !lines[i].StartsWith("@@") && !lines[i].StartsWith("--- "))
        {
            hunkLines.Add(lines[i]);
            i++;
        }

        return new PatchHunk(origRange.start, origRange.count, modRange.start, modRange.count, hunkLines);
    }

    private (int start, int count) ParseRange(string range)
    {
        var parts = range.Split(',');
        var start = int.Parse(parts[0]);
        var count = parts.Length > 1 ? int.Parse(parts[1]) : 1;
        return (start, count);
    }

    private static void ValidatePatchPath(string patchPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(patchPath))
            throw new InvalidOperationException($"{parameterName} is empty.");

        if (Path.IsPathRooted(patchPath))
            throw new InvalidOperationException($"{parameterName} must be a relative path.");

        var segments = patchPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
            throw new InvalidOperationException($"{parameterName} must not contain path traversal.");
    }

    private void ApplyFilePatch(FilePatch filePatch, string sourceDir, string outputDir)
    {
        ValidatePatchPath(filePatch.Path, nameof(filePatch.Path));
        if (!string.IsNullOrEmpty(filePatch.OriginalPath))
        {
            ValidatePatchPath(filePatch.OriginalPath, nameof(filePatch.OriginalPath));
        }

        var sourcePath = Path.Combine(sourceDir, filePatch.OriginalPath);
        var outputPath = Path.Combine(outputDir, filePatch.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Handle new files (no original)
        if (!File.Exists(sourcePath))
        {
            var newLines = new List<string>();
            foreach (var hunk in filePatch.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.StartsWith('+'))
                    {
                        newLines.Add(line[1..]);
                    }
                }
            }
            File.WriteAllLines(outputPath, newLines);
            return;
        }

        var originalLines = File.ReadAllLines(sourcePath).ToList();
        var offset = 0;

        foreach (var hunk in filePatch.Hunks)
        {
            var lineIndex = hunk.OriginalStart - 1 + offset; // 1-based to 0-based

            // Remove original lines
            var removedCount = 0;
            var toProcess = new List<string>(hunk.Lines);

            // First pass: remove lines marked with '-' and context
            var newContent = new List<string>();
            int origIdx = lineIndex;

            foreach (var line in toProcess)
            {
                if (line.StartsWith('-'))
                {
                    // Verify context matches (skip if not - best effort)
                    origIdx++;
                    removedCount++;
                }
                else if (line.StartsWith('+'))
                {
                    newContent.Add(line[1..]);
                }
                else if (line.StartsWith(' '))
                {
                    newContent.Add(line[1..]);
                    origIdx++;
                    removedCount++;
                }
                else
                {
                    // No prefix - treat as context
                    newContent.Add(line);
                    origIdx++;
                    removedCount++;
                }
            }

            // Apply: remove old, insert new
            if (lineIndex >= 0 && lineIndex <= originalLines.Count)
            {
                var removeCount = Math.Min(removedCount, originalLines.Count - lineIndex);
                if (removeCount > 0)
                {
                    originalLines.RemoveRange(lineIndex, removeCount);
                }
                originalLines.InsertRange(lineIndex, newContent);
                offset += newContent.Count - removeCount;
            }
        }

        File.WriteAllLines(outputPath, originalLines);
    }

    private record FilePatch(string Path, string OriginalPath, List<PatchHunk> Hunks, bool IsDelete);
    private record PatchHunk(int OriginalStart, int OriginalCount, int ModifiedStart, int ModifiedCount, List<string> Lines);
}

public class PatchApplicationResult
{
    public List<string> AppliedFiles { get; } = [];
    public List<string> DeletedFiles { get; } = [];
    public List<(string File, string Error)> FailedFiles { get; } = [];
    public bool Success => FailedFiles.Count == 0;
}
