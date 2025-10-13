namespace NuGetToCompLog.Domain;

/// <summary>
/// Information about a reference assembly that has been acquired.
/// </summary>
public record ReferenceAssemblyInfo(
    string FileName,
    string LocalPath,
    ReferenceSource Source)
{
    /// <summary>
    /// Gets the assembly name without extension.
    /// </summary>
    public string AssemblyName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}

/// <summary>
/// Source of a reference assembly.
/// </summary>
public enum ReferenceSource
{
    Framework,
    NuGetPackage,
    LocalSdk
}
