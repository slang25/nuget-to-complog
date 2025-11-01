using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Exceptions;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Service for discovering PDB files associated with assemblies.
/// </summary>
public class PdbDiscoveryService
{
    private readonly IFileSystemService _fileSystem;

    public PdbDiscoveryService(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Finds a PDB file for an assembly, checking multiple locations.
    /// </summary>
    public string? FindPdbFile(string assemblyPath, string workingDirectory)
    {
        // Extract the TFM from the assembly path for better matching
        var extractedDir = Path.Combine(workingDirectory, "extracted");
        string? targetFramework = null;

        if (assemblyPath.StartsWith(extractedDir))
        {
            var relativePath = Path.GetRelativePath(extractedDir, assemblyPath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length > 1)
            {
                targetFramework = parts[1]; // lib/net8.0/Package.dll -> net8.0
            }
        }

        // Get PDB filename from assembly's debug directory
        var pdbFileName = GetPdbFileName(assemblyPath);
        var dllBaseName = Path.GetFileNameWithoutExtension(assemblyPath);
        var expectedPdbName = pdbFileName != null ? Path.GetFileName(pdbFileName) : $"{dllBaseName}.pdb";
        
        // Try multiple locations
        // 1. Same directory as assembly (highest priority)
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (assemblyDir != null)
        {
            // First, try the PDB name from the CodeView debug directory (if available)
            if (pdbFileName != null)
            {
                var pdbPath = Path.Combine(assemblyDir, Path.GetFileName(pdbFileName));
                if (_fileSystem.FileExists(pdbPath))
                    return pdbPath;
            }
            
            // Fallback: check for PDB with same base name as DLL (common in nupkg packages)
            var fallbackPdbPath = Path.Combine(assemblyDir, $"{dllBaseName}.pdb");
            if (_fileSystem.FileExists(fallbackPdbPath))
                return fallbackPdbPath;
        }

        // 2. Symbols package extraction directory - prefer matching TFM
        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (_fileSystem.DirectoryExists(symbolsDir))
        {
            var pdbPaths = _fileSystem.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories);

            // First, try to find PDB with matching TFM
            if (targetFramework != null)
            {
                var tfmMatch = pdbPaths.FirstOrDefault(p =>
                {
                    var name = Path.GetFileName(p);
                    if (!name.Equals(expectedPdbName, StringComparison.OrdinalIgnoreCase))
                        return false;

                    var pdbRelativePath = Path.GetRelativePath(symbolsDir, p);
                    return pdbRelativePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase);
                });

                if (tfmMatch != null)
                    return tfmMatch;
            }

            // Fallback: find any PDB with matching name
            var match = pdbPaths.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(expectedPdbName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // 3. Original package extraction directory - prefer matching TFM
        if (_fileSystem.DirectoryExists(extractedDir))
        {
            var pdbPaths = _fileSystem.GetFiles(extractedDir, "*.pdb", SearchOption.AllDirectories);

            if (targetFramework != null)
            {
                var tfmMatch = pdbPaths.FirstOrDefault(p =>
                {
                    var name = Path.GetFileName(p);
                    if (!name.Equals(expectedPdbName, StringComparison.OrdinalIgnoreCase))
                        return false;

                    var pdbRelativePath = Path.GetRelativePath(extractedDir, p);
                    return pdbRelativePath.Contains(targetFramework, StringComparison.OrdinalIgnoreCase);
                });

                if (tfmMatch != null)
                    return tfmMatch;
            }

            var match = pdbPaths.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(expectedPdbName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return null;
    }

    /// <summary>
    /// Checks if an assembly has an embedded PDB.
    /// </summary>
    public bool HasEmbeddedPdb(string assemblyPath)
    {
        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(peStream);

            var embeddedPdb = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            return embeddedPdb.DataSize > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if an assembly has a reproducible/deterministic marker.
    /// </summary>
    public bool HasReproducibleMarker(string assemblyPath)
    {
        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(peStream);

            return peReader.ReadDebugDirectory()
                .Any(d => d.Type == DebugDirectoryEntryType.Reproducible);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the PDB filename referenced by an assembly.
    /// </summary>
    private string? GetPdbFileName(string assemblyPath)
    {
        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(peStream);

            var codeView = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.CodeView);

            if (codeView.DataSize > 0)
            {
                var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeView);
                return codeViewData.Path;
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }
}
