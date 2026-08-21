namespace NuGetToCompLog.Services;

/// <summary>
/// Maps PDB document paths to local relative paths under the sources directory, preserving
/// the original directory structure so a single /pathmap entry can round-trip them.
///
/// The project root is derived from the PDB path recorded in the assembly's CodeView entry
/// (everything up to the /obj/ segment, e.g. /_/src/Serilog/obj/Release/net9.0/Serilog.pdb
/// gives /_/src/Serilog/). Documents under that root — including compiler-generated files in
/// obj/ — keep their structure relative to it, which is what makes source ordering and
/// /pathmap reproduction exact.
/// </summary>
public sealed class SourcePathMapper
{
    /// <summary>
    /// The original project root all mapped documents are relative to, normalized to forward
    /// slashes with a trailing slash (e.g. "/_/src/Serilog/"). Null when no root could be derived.
    /// </summary>
    public string? RootPrefix { get; }

    private SourcePathMapper(string? rootPrefix)
    {
        RootPrefix = rootPrefix;
    }

    public static SourcePathMapper Create(IEnumerable<string> documentPaths, string? pdbPath)
    {
        if (!string.IsNullOrEmpty(pdbPath))
        {
            var normalized = Normalize(pdbPath);
            var objIndex = normalized.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase);
            if (objIndex > 0)
            {
                return new SourcePathMapper(normalized[..(objIndex + 1)]);
            }

            // No obj/ segment: fall back to the PDB's directory.
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                return new SourcePathMapper(normalized[..(lastSlash + 1)]);
            }
        }

        return new SourcePathMapper(LongestCommonDirectoryPrefix(documentPaths));
    }

    /// <summary>
    /// Maps a PDB document path to a relative path under the sources directory.
    /// Documents outside the project root land under _external/ (best effort; a
    /// diagnostic should be raised for those since /pathmap can't round-trip them).
    /// </summary>
    public string MapToLocal(string documentPath)
    {
        var normalized = Normalize(documentPath);

        if (RootPrefix != null && normalized.StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[RootPrefix.Length..];
        }

        var trimmed = normalized.TrimStart('/');
        if (trimmed.Length > 1 && trimmed[1] == ':')
        {
            // Windows drive-rooted path: C:/src/... -> C/src/...
            trimmed = trimmed[0] + trimmed[2..];
        }

        return ExternalPrefix + trimmed;
    }

    /// <summary>
    /// Derives the /pathmap keys that map the local _external/ layout back to the original
    /// document roots. <see cref="MapToLocal"/> is lossy at the root - it strips the leading
    /// '/' of a Unix path and the ':' of a Windows drive path - so a single "_external/" =&gt; "/"
    /// entry would turn "C:/src/a.cs" into "/C/src/a.cs". Recovering the roots per document
    /// (by the longest suffix its local and original paths share at a '/' boundary) yields
    /// "_external/" =&gt; "/" for Unix and "_external/C/" =&gt; "C:/" for a Windows drive.
    /// Ordered longest key first, since csc applies the first matching entry.
    /// </summary>
    public static List<(string LocalPrefix, string OriginalPrefix)> DeriveExternalPathMaps(
        IEnumerable<(string LocalPath, string DocumentPath)> documents)
    {
        var byLocalPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (localPath, documentPath) in documents)
        {
            var local = Normalize(localPath);
            if (!local.StartsWith(ExternalPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var original = Normalize(documentPath);
            int i = local.Length, j = original.Length, cutLocal = -1, cutOriginal = -1;
            while (i > 0 && j > 0 && local[i - 1] == original[j - 1])
            {
                i--;
                j--;
                if (local[i] == '/')
                {
                    cutLocal = i;
                    cutOriginal = j;
                }
            }

            // No shared directory boundary: the document path isn't what produced this local
            // path, so there's nothing to map it back to.
            if (cutLocal < ExternalPrefix.Length - 1)
            {
                continue;
            }

            byLocalPrefix.TryAdd(local[..(cutLocal + 1)], original[..(cutOriginal + 1)]);
        }

        return byLocalPrefix
            .OrderByDescending(kvp => kvp.Key.Length)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Whether the document maps cleanly under the project root (and therefore round-trips via /pathmap).
    /// </summary>
    public bool IsUnderRoot(string documentPath) =>
        RootPrefix != null && Normalize(documentPath).StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase);

    private const string ExternalPrefix = "_external/";

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? LongestCommonDirectoryPrefix(IEnumerable<string> paths)
    {
        string? prefix = null;
        foreach (var path in paths)
        {
            var dir = Normalize(path);
            var lastSlash = dir.LastIndexOf('/');
            if (lastSlash < 0)
            {
                return null;
            }
            dir = dir[..(lastSlash + 1)];

            if (prefix == null)
            {
                prefix = dir;
                continue;
            }

            while (prefix.Length > 0 && !dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // Shrink to the previous '/' boundary.
                var cut = prefix.LastIndexOf('/', prefix.Length - 2);
                if (cut < 0)
                {
                    return null;
                }
                prefix = prefix[..(cut + 1)];
            }
        }

        return string.IsNullOrEmpty(prefix) ? null : prefix;
    }
}
