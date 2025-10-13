using NuGetToCompLog.Domain;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for reading PDB files.
/// </summary>
public interface IPdbReader
{
    /// <summary>
    /// Finds a PDB file for an assembly.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly.</param>
    /// <param name="workingDirectory">Working directory to search in.</param>
    /// <returns>Path to the PDB file, or null if not found.</returns>
    Task<string?> FindPdbAsync(string assemblyPath, string workingDirectory);
    
    /// <summary>
    /// Checks if an assembly has an embedded PDB.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly.</param>
    /// <returns>True if the assembly has an embedded PDB.</returns>
    bool HasEmbeddedPdb(string assemblyPath);
    
    /// <summary>
    /// Extracts metadata from a PDB file (embedded or external).
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly.</param>
    /// <param name="pdbPath">Path to external PDB, or null for embedded.</param>
    /// <param name="hasReproducibleMarker">Whether the PE has a reproducible marker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PDB metadata.</returns>
    Task<PdbMetadata> ExtractMetadataAsync(
        string assemblyPath, 
        string? pdbPath, 
        bool hasReproducibleMarker,
        CancellationToken cancellationToken = default);
}
