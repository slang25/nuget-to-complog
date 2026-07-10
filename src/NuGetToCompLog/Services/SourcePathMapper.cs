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

        return "_external/" + trimmed;
    }

    /// <summary>
    /// Whether the document maps cleanly under the project root (and therefore round-trips via /pathmap).
    /// </summary>
    public bool IsUnderRoot(string documentPath) =>
        RootPrefix != null && Normalize(documentPath).StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase);

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
