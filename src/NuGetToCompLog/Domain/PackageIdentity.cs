namespace NuGetToCompLog.Domain;

/// <summary>
/// Immutable identifier for a NuGet package.
/// </summary>
public record PackageIdentity(string Id, string Version)
{
    /// <summary>
    /// Gets the package key in the format "Id/Version".
    /// </summary>
    public string Key => $"{Id}/{Version}";
    
    /// <summary>
    /// Gets the package filename (e.g., "Package.1.0.0.nupkg").
    /// </summary>
    public string FileName => $"{Id}.{Version}.nupkg";
    
    /// <summary>
    /// Gets the symbols package filename (e.g., "Package.1.0.0.snupkg").
    /// </summary>
    public string SymbolsFileName => $"{Id}.{Version}.snupkg";
}
