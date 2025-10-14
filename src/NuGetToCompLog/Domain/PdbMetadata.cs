namespace NuGetToCompLog.Domain;

/// <summary>
/// Metadata extracted from a portable PDB file.
/// </summary>
public record PdbMetadata(
    string? PdbPath,
    bool IsEmbedded,
    List<string> CompilerArguments,
    List<MetadataReference> MetadataReferences,
    List<SourceFileInfo> SourceFiles,
    string? SourceLinkJson,
    List<EmbeddedResourceInfo> EmbeddedResources)
{
    public PdbMetadata() : this(
        null,
        false,
        new List<string>(),
        new List<MetadataReference>(),
        new List<SourceFileInfo>(),
        null,
        new List<EmbeddedResourceInfo>())
    {
    }
}
