namespace NuGetToCompLog.Domain;

/// <summary>
/// Contains compiler arguments and metadata extracted from a PDB.
/// </summary>
public record CompilationInfo(
    List<string> CompilerArguments,
    List<MetadataReference> MetadataReferences,
    string? TargetFramework,
    bool HasEmbeddedPdb,
    bool HasDeterministicMarker)
{
    public CompilationInfo() : this(
        new List<string>(),
        new List<MetadataReference>(),
        null,
        false,
        false)
    {
    }
}
