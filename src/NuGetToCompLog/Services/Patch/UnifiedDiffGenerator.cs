namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Generates unified diff output compatible with `git apply` and `patch`.
/// Uses a simple LCS-based diff algorithm.
/// </summary>
public class UnifiedDiffGenerator
{
    private const int ContextLines = 3;

    /// <summary>
    /// Generates a unified diff comparing two directory trees.
    /// Returns the diff content and a summary of changes.
    /// </summary>
    public DiffResult GenerateDiff(string originalDir, string modifiedDir)
    {
        var result = new DiffResult();
        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all relative paths from both directories
        if (Directory.Exists(originalDir))
        {
            foreach (var file in Directory.GetFiles(originalDir, "*", SearchOption.AllDirectories))
            {
                allFiles.Add(Path.GetRelativePath(originalDir, file));
            }
        }

        if (Directory.Exists(modifiedDir))
        {
            foreach (var file in Directory.GetFiles(modifiedDir, "*", SearchOption.AllDirectories))
            {
                allFiles.Add(Path.GetRelativePath(modifiedDir, file));
            }
        }

        foreach (var relativePath in allFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var originalFile = Path.Combine(originalDir, relativePath);
            var modifiedFile = Path.Combine(modifiedDir, relativePath);

            var originalExists = File.Exists(originalFile);
            var modifiedExists = File.Exists(modifiedFile);

            if (originalExists && modifiedExists)
            {
                var originalLines = File.ReadAllLines(originalFile);
                var modifiedLines = File.ReadAllLines(modifiedFile);

                if (originalLines.SequenceEqual(modifiedLines))
                    continue;

                var fileDiff = GenerateFileDiff(relativePath, originalLines, modifiedLines);
                result.FileDiffs.Add(fileDiff);
                result.ModifiedFiles.Add(relativePath);
            }
            else if (!originalExists && modifiedExists)
            {
                var modifiedLines = File.ReadAllLines(modifiedFile);
                var fileDiff = GenerateFileDiff(relativePath, [], modifiedLines);
                result.FileDiffs.Add(fileDiff);
                result.AddedFiles.Add(relativePath);
            }
            else if (originalExists && !modifiedExists)
            {
                var originalLines = File.ReadAllLines(originalFile);
                var fileDiff = GenerateFileDiff(relativePath, originalLines, []);
                result.FileDiffs.Add(fileDiff);
                result.DeletedFiles.Add(relativePath);
            }
        }

        return result;
    }

    /// <summary>
    /// Formats the diff result as a unified diff string.
    /// </summary>
    public string FormatDiff(DiffResult result)
    {
        return string.Join("\n", result.FileDiffs);
    }

    private string GenerateFileDiff(string relativePath, string[] originalLines, string[] modifiedLines)
    {
        var hunks = ComputeHunks(originalLines, modifiedLines);
        var lines = new List<string>();

        // Normalize to forward slashes for git apply compatibility
        var normalizedPath = relativePath.Replace('\\', '/');

        var isAddedFile = originalLines.Length == 0 && modifiedLines.Length > 0;
        var isDeletedFile = originalLines.Length > 0 && modifiedLines.Length == 0;

        var originalHeader = isAddedFile ? "--- /dev/null" : $"--- a/{normalizedPath}";
        var modifiedHeader = isDeletedFile ? "+++ /dev/null" : $"+++ b/{normalizedPath}";

        lines.Add(originalHeader);
        lines.Add(modifiedHeader);

        foreach (var hunk in hunks)
        {
            lines.Add(hunk.Header);
            lines.AddRange(hunk.Lines);
        }

        return string.Join("\n", lines);
    }

    private List<DiffHunk> ComputeHunks(string[] original, string[] modified)
    {
        var edits = ComputeEdits(original, modified);
        var hunks = new List<DiffHunk>();

        if (edits.Count == 0)
            return hunks;

        // Group edits into hunks with context
        var currentHunkEdits = new List<Edit>();
        int? hunkStart = null;

        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];

            if (hunkStart == null)
            {
                hunkStart = i;
                currentHunkEdits.Add(edit);
            }
            else
            {
                // Check if this edit is close enough to merge into the current hunk
                var prevEdit = edits[i - 1];
                var gap = edit.OriginalStart - (prevEdit.OriginalStart + prevEdit.OriginalCount);

                if (gap <= ContextLines * 2)
                {
                    currentHunkEdits.Add(edit);
                }
                else
                {
                    hunks.Add(BuildHunk(original, modified, currentHunkEdits));
                    currentHunkEdits = [edit];
                    hunkStart = i;
                }
            }
        }

        if (currentHunkEdits.Count > 0)
        {
            hunks.Add(BuildHunk(original, modified, currentHunkEdits));
        }

        return hunks;
    }

    private static DiffHunk BuildHunk(string[] original, string[] modified, List<Edit> edits)
    {
        var firstEdit = edits[0];
        var lastEdit = edits[^1];

        var contextBefore = Math.Min(
            ContextLines,
            Math.Min(firstEdit.OriginalStart, firstEdit.ModifiedStart));
        var contextAfter = Math.Min(
            ContextLines,
            Math.Min(
                original.Length - (lastEdit.OriginalStart + lastEdit.OriginalCount),
                modified.Length - (lastEdit.ModifiedStart + lastEdit.ModifiedCount)));

        var origStart = Math.Max(0, firstEdit.OriginalStart - contextBefore);
        var modStart = Math.Max(0, firstEdit.ModifiedStart - contextBefore);

        var lines = new List<string>();

        // Add context before first edit
        for (int i = origStart; i < firstEdit.OriginalStart; i++)
        {
            lines.Add($" {original[i]}");
        }

        // Process each edit
        for (int e = 0; e < edits.Count; e++)
        {
            var edit = edits[e];

            // Add removed lines
            for (int i = edit.OriginalStart; i < edit.OriginalStart + edit.OriginalCount; i++)
            {
                lines.Add($"-{original[i]}");
            }

            // Add added lines
            for (int i = edit.ModifiedStart; i < edit.ModifiedStart + edit.ModifiedCount; i++)
            {
                lines.Add($"+{modified[i]}");
            }

            // Add context between edits
            if (e < edits.Count - 1)
            {
                var nextEdit = edits[e + 1];
                // Show all context lines between edits
                for (int i = edit.OriginalStart + edit.OriginalCount; i < nextEdit.OriginalStart; i++)
                {
                    lines.Add($" {original[i]}");
                }
            }
        }

        // Add context after last edit
        var afterStart = lastEdit.OriginalStart + lastEdit.OriginalCount;
        var afterEnd = Math.Min(afterStart + contextAfter, original.Length);
        for (int i = afterStart; i < afterEnd; i++)
        {
            lines.Add($" {original[i]}");
        }

        // Compute hunk header counts
        var origCount = lines.Count(l => l.StartsWith(' ') || l.StartsWith('-'));
        var modCount = lines.Count(l => l.StartsWith(' ') || l.StartsWith('+'));

        var origHeaderStart = origCount == 0 ? 0 : origStart + 1;
        var modHeaderStart = modCount == 0 ? 0 : modStart + 1;
        var header = $"@@ -{origHeaderStart},{origCount} +{modHeaderStart},{modCount} @@";

        return new DiffHunk(header, lines);
    }

    /// <summary>
    /// Computes the list of edits (insertions/deletions/replacements) between two sequences.
    /// Uses a simple LCS-based approach.
    /// </summary>
    private List<Edit> ComputeEdits(string[] original, string[] modified)
    {
        // Compute LCS using standard DP approach
        var lcs = ComputeLcs(original, modified);
        var edits = new List<Edit>();

        int oi = 0, mi = 0, li = 0;

        while (oi < original.Length || mi < modified.Length)
        {
            if (li < lcs.Count && oi < original.Length && mi < modified.Length
                && original[oi] == lcs[li] && modified[mi] == lcs[li])
            {
                // Common line - skip
                oi++;
                mi++;
                li++;
            }
            else
            {
                // Collect a contiguous block of changes
                var origStart = oi;
                var modStart = mi;

                while (oi < original.Length && (li >= lcs.Count || original[oi] != lcs[li]))
                {
                    oi++;
                }

                while (mi < modified.Length && (li >= lcs.Count || modified[mi] != lcs[li]))
                {
                    mi++;
                }

                if (oi > origStart || mi > modStart)
                {
                    edits.Add(new Edit(origStart, oi - origStart, modStart, mi - modStart));
                }
            }
        }

        return edits;
    }

    private List<string> ComputeLcs(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;

        // Optimization: for very large files, use a simpler approach
        if ((long)m * n > 10_000_000)
        {
            return ComputeLcsGreedy(a, b);
        }

        var dp = new int[m + 1, n + 1];

        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                if (a[i] == b[j])
                {
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }
        }

        // Backtrack to find LCS
        var result = new List<string>();
        int ai = 0, bi = 0;

        while (ai < m && bi < n)
        {
            if (a[ai] == b[bi])
            {
                result.Add(a[ai]);
                ai++;
                bi++;
            }
            else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
            {
                ai++;
            }
            else
            {
                bi++;
            }
        }

        return result;
    }

    /// <summary>
    /// Greedy LCS for large files - less optimal but uses O(n) memory.
    /// </summary>
    private List<string> ComputeLcsGreedy(string[] a, string[] b)
    {
        var bIndex = new Dictionary<string, List<int>>();
        for (int i = 0; i < b.Length; i++)
        {
            if (!bIndex.TryGetValue(b[i], out var list))
            {
                list = [];
                bIndex[b[i]] = list;
            }
            list.Add(i);
        }

        var result = new List<string>();
        int lastB = -1;

        foreach (var line in a)
        {
            if (bIndex.TryGetValue(line, out var positions))
            {
                // Find the first position after lastB
                var pos = positions.FindIndex(p => p > lastB);
                if (pos >= 0)
                {
                    result.Add(line);
                    lastB = positions[pos];
                }
            }
        }

        return result;
    }

    private record Edit(int OriginalStart, int OriginalCount, int ModifiedStart, int ModifiedCount);
    private record DiffHunk(string Header, List<string> Lines);
}

public class DiffResult
{
    public List<string> FileDiffs { get; } = [];
    public List<string> AddedFiles { get; } = [];
    public List<string> ModifiedFiles { get; } = [];
    public List<string> DeletedFiles { get; } = [];

    public bool HasChanges => FileDiffs.Count > 0;
    public int TotalChangedFiles => AddedFiles.Count + ModifiedFiles.Count + DeletedFiles.Count;
}
