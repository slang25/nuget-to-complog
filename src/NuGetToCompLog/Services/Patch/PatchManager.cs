using System.Text.Json;
using System.Text.Json.Serialization;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Manages patch directory discovery, metadata reading, and patch file storage.
/// </summary>
public class PatchManager
{
    private readonly IConsoleWriter _console;

    public PatchManager(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Finds the patch directory for a given package ID.
    /// If version is not specified and only one version exists, uses that.
    /// </summary>
    public string? FindPatchDirectory(string packageId, string? version = null, string? patchesBaseDir = null)
    {
        var baseDir = patchesBaseDir ?? Path.Combine(Directory.GetCurrentDirectory(), "patches");

        if (!Directory.Exists(baseDir))
            return null;

        if (version != null)
        {
            var expectedDirectoryName = $"{packageId}+{version}";
            var patchDirectories = Directory.GetDirectories(baseDir);
            return patchDirectories.FirstOrDefault(d =>
                string.Equals(Path.GetFileName(d), expectedDirectoryName, StringComparison.OrdinalIgnoreCase));
        }

        // Find all directories matching this package ID (case-insensitive for cross-platform compat)
        var matches = Directory.GetDirectories(baseDir)
            .Where(d => Path.GetFileName(d).StartsWith($"{packageId}+", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return null;

        if (matches.Count == 1)
            return matches[0];

        _console.MarkupLine($"[yellow]\u26a0[/] Multiple versions found for {packageId}:");
        foreach (var match in matches)
        {
            _console.MarkupLine($"  [dim]{Path.GetFileName(match)}[/]");
        }
        _console.MarkupLine("[dim]Please specify a version[/]");
        return null;
    }

    /// <summary>
    /// Reads patch metadata from a patch directory.
    /// </summary>
    public async Task<PatchMetadata?> ReadPatchMetadataAsync(string patchDir)
    {
        var metadataFile = Path.Combine(patchDir, "patch-metadata.json");
        if (!File.Exists(metadataFile))
            return null;

        var json = await File.ReadAllTextAsync(metadataFile);
        return JsonSerializer.Deserialize<PatchMetadata>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Lists all patch files in the patches directory.
    /// </summary>
    public List<string> ListPatchFiles(string? patchesBaseDir = null)
    {
        var baseDir = patchesBaseDir ?? Path.Combine(Directory.GetCurrentDirectory(), "patches");
        if (!Directory.Exists(baseDir))
            return [];

        return Directory.GetFiles(baseDir, "*.patch").ToList();
    }

    /// <summary>
    /// Lists all ejected package directories.
    /// </summary>
    public List<string> ListEjectedPackages(string? patchesBaseDir = null)
    {
        var baseDir = patchesBaseDir ?? Path.Combine(Directory.GetCurrentDirectory(), "patches");
        if (!Directory.Exists(baseDir))
            return [];

        return Directory.GetDirectories(baseDir)
            .Where(d => File.Exists(Path.Combine(d, "patch-metadata.json")))
            .ToList();
    }
}

public record PatchMetadata
{
    public string PackageId { get; init; } = "";
    public string Version { get; init; } = "";
    public string? TargetFramework { get; init; }
    public string? AssemblyName { get; init; }
    public List<string> Assemblies { get; init; } = [];
    public string? EjectedAt { get; init; }
    public string? ToolVersion { get; init; }
}
