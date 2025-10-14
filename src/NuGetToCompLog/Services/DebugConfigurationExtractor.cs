using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Services;

/// <summary>
/// Extracts debug configuration from an assembly to determine the correct /debug: flags.
/// </summary>
public class DebugConfigurationExtractor
{
    /// <summary>
    /// Extracts debug configuration from an assembly's PE structure.
    /// </summary>
    public static DebugConfiguration ExtractDebugConfiguration(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        
        var debugEntries = peReader.ReadDebugDirectory();
        
        bool hasCodeView = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.CodeView);
        bool hasEmbeddedPdb = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        bool hasPdbChecksum = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.PdbChecksum);
        bool hasReproducible = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.Reproducible);
        
        // Extract PDB path if CodeView entry exists
        string? pdbPath = null;
        if (hasCodeView)
        {
            var codeViewEntry = debugEntries.First(e => e.Type == DebugDirectoryEntryType.CodeView);
            var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
            pdbPath = codeViewData.Path;
        }
        
        // Determine debug type based on entries
        var debugType = DetermineDebugType(hasCodeView, hasEmbeddedPdb, hasPdbChecksum);
        
        return new DebugConfiguration
        {
            DebugType = debugType,
            HasCodeView = hasCodeView,
            HasEmbeddedPdb = hasEmbeddedPdb,
            HasPdbChecksum = hasPdbChecksum,
            HasReproducible = hasReproducible,
            PdbPath = pdbPath,
            DebugEntryCount = debugEntries.Length
        };
    }
    
    private static DebugType DetermineDebugType(bool hasCodeView, bool hasEmbeddedPdb, bool hasPdbChecksum)
    {
        // Embedded PDB with checksum = /debug:embedded
        if (hasEmbeddedPdb && hasPdbChecksum)
        {
            return DebugType.Embedded;
        }
        
        // Embedded PDB without checksum = /debug:portable (embedded)
        if (hasEmbeddedPdb && !hasPdbChecksum)
        {
            return DebugType.PortableEmbedded;
        }
        
        // External PDB reference = /debug:portable (external)
        if (hasCodeView && !hasEmbeddedPdb)
        {
            return DebugType.PortableExternal;
        }
        
        // No debug info
        return DebugType.None;
    }
}

/// <summary>
/// Debug configuration extracted from an assembly.
/// </summary>
public class DebugConfiguration
{
    public DebugType DebugType { get; set; }
    public bool HasCodeView { get; set; }
    public bool HasEmbeddedPdb { get; set; }
    public bool HasPdbChecksum { get; set; }
    public bool HasReproducible { get; set; }
    public string? PdbPath { get; set; }
    public int DebugEntryCount { get; set; }
    
    /// <summary>
    /// Converts the debug configuration to compiler flags.
    /// </summary>
    /// <param name="pdbOutputPath">Optional PDB output path. If not provided and external PDB is needed, caller must add /pdb: separately.</param>
    public IEnumerable<string> ToCompilerFlags(string? pdbOutputPath = null)
    {
        var flags = new List<string>();
        
        switch (DebugType)
        {
            case DebugType.Embedded:
                // Embedded PDB with checksum
                flags.Add("/debug:embedded");
                break;
                
            case DebugType.PortableEmbedded:
                // Embedded PDB without checksum (less common)
                flags.Add("/debug:portable");
                flags.Add("/embed");
                break;
                
            case DebugType.PortableExternal:
                // External portable PDB
                flags.Add("/debug:portable");
                // Add the PDB path if provided
                // The caller should provide a writable path (e.g., "output/Package.pdb")
                // and use /pathmap to make it appear as the original path in metadata
                if (!string.IsNullOrEmpty(pdbOutputPath))
                {
                    flags.Add($"/pdb:{pdbOutputPath}");
                }
                break;
                
            case DebugType.None:
                // No debug info - don't add any debug flags
                // The /deterministic+ flag should already be present
                break;
        }
        
        return flags;
    }
    
    public override string ToString()
    {
        return $"DebugType: {DebugType}, Entries: {DebugEntryCount}, " +
               $"CodeView: {HasCodeView}, EmbeddedPdb: {HasEmbeddedPdb}, " +
               $"PdbChecksum: {HasPdbChecksum}, Reproducible: {HasReproducible}";
    }
}

/// <summary>
/// Type of debug information in the assembly.
/// </summary>
public enum DebugType
{
    /// <summary>No debug information</summary>
    None,
    
    /// <summary>Portable PDB, external file (/debug:portable)</summary>
    PortableExternal,
    
    /// <summary>Portable PDB, embedded (/debug:portable /embed)</summary>
    PortableEmbedded,
    
    /// <summary>Embedded PDB with checksum (/debug:embedded)</summary>
    Embedded
}
