namespace NuGetToCompLog.Domain;

/// <summary>
/// Result of the package analysis pipeline - captures everything needed
/// to either create a complog or generate an editable project.
/// </summary>
public record PackageExtractionResult(
    PackageIdentity Package,
    string WorkingDirectory,
    string ExtractPath,
    string? SelectedTfm,
    List<string> SelectedAssemblies,
    string? CompilerArgsFile,
    string? MetadataRefsFile,
    string SourcesDirectory,
    string? ResourcesDirectory);
