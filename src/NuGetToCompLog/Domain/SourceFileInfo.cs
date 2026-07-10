namespace NuGetToCompLog.Domain;

/// <summary>
/// Information about a source file extracted from a PDB.
/// </summary>
/// <param name="Path">The document path as recorded in the PDB (e.g. /_/src/Serilog/Log.cs).</param>
/// <param name="Content">Decoded text of the embedded source, if present.</param>
/// <param name="ContentBytes">Raw bytes of the embedded source, if present. Preserves BOM and
/// line endings exactly as the original compiler saw them, unlike <paramref name="Content"/>.</param>
/// <param name="IsEmbedded">Whether the source is embedded in the PDB.</param>
/// <param name="SourceLinkUrl">Resolved Source Link URL, if any.</param>
/// <param name="Hash">The document checksum recorded in the PDB.</param>
/// <param name="HashAlgorithm">Checksum algorithm name ("sha256" or "sha1"), or null if unknown.</param>
/// <param name="LocalPath">Relative path (under the sources directory) where this file is stored locally.</param>
public record SourceFileInfo(
    string Path,
    string? Content,
    bool IsEmbedded,
    string? SourceLinkUrl,
    byte[]? ContentBytes = null,
    byte[]? Hash = null,
    string? HashAlgorithm = null,
    string? LocalPath = null)
{
    /// <summary>
    /// Gets the filename without path.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Gets whether the source content has been loaded.
    /// </summary>
    public bool HasContent => Content != null || ContentBytes != null;
}
