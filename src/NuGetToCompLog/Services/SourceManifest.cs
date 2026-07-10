using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetToCompLog.Services;

/// <summary>
/// One document from the PDB Documents table, in table order.
/// </summary>
public record SourceManifestEntry(
    string DocumentPath,
    string LocalPath,
    string? HashAlgorithm,
    string? Hash,
    bool IsEmbedded);

/// <summary>
/// Persists the PDB Documents table (order, paths, checksums, embedded flags) so complog
/// creation can list sources in the exact order the original compiler saw them. The order
/// matters for byte-for-byte reproduction: assembly attributes and metadata heap layout
/// follow source order, so an alphabetical listing produces a different (if semantically
/// identical) assembly.
/// </summary>
public record SourceManifest(
    List<SourceManifestEntry> Documents,
    string? PathMapRoot)
{
    public const string FileName = "sources-manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SaveAsync(string workingDirectory)
    {
        var path = Path.Combine(workingDirectory, FileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Options));
    }

    public static SourceManifest? TryLoad(string workingDirectory)
    {
        var path = Path.Combine(workingDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SourceManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
