namespace NuGetToCompLog.Domain;

/// <summary>
/// Information about a source file extracted from a PDB.
/// </summary>
public record SourceFileInfo(
    string Path,
    string? Content,
    bool IsEmbedded,
    string? SourceLinkUrl)
{
    /// <summary>
    /// Gets the filename without path.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path);
    
    /// <summary>
    /// Gets whether the source content has been loaded.
    /// </summary>
    public bool HasContent => Content != null;
}
