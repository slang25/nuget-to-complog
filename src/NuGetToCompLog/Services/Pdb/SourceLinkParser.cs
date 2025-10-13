using System.Text.Json;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Parses Source Link JSON configuration and maps local paths to URLs.
/// </summary>
public class SourceLinkParser
{
    /// <summary>
    /// Parses Source Link JSON and extracts document mappings.
    /// </summary>
    public Dictionary<string, string> ParseMappings(string? sourceLinkJson)
    {
        var mappings = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(sourceLinkJson))
        {
            return mappings;
        }

        try
        {
            using var doc = JsonDocument.Parse(sourceLinkJson);
            if (doc.RootElement.TryGetProperty("documents", out var documents))
            {
                foreach (var prop in documents.EnumerateObject())
                {
                    mappings[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return mappings;
    }

    /// <summary>
    /// Maps a local file path to a Source Link URL using the mappings.
    /// </summary>
    public string? MapPathToUrl(string localPath, Dictionary<string, string> mappings)
    {
        var normalizedPath = localPath.Replace('\\', '/');

        foreach (var (pattern, urlTemplate) in mappings)
        {
            var normalizedPattern = pattern.Replace('\\', '/');

            var wildcardIndex = normalizedPattern.IndexOf('*');
            if (wildcardIndex < 0) continue;

            var prefix = normalizedPattern.Substring(0, wildcardIndex);

            if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = normalizedPath.Substring(prefix.Length);
                var url = urlTemplate.Replace("*", relativePath);
                return url;
            }
        }

        return null;
    }
}
