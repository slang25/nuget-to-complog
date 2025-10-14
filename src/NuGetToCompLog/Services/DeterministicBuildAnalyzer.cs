using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Spectre.Console;

namespace NuGetToCompLog.Services;

/// <summary>
/// Analyzes assemblies to extract information needed for deterministic builds.
/// </summary>
public class DeterministicBuildAnalyzer
{
    /// <summary>
    /// Extracts all deterministic build metadata from an assembly.
    /// </summary>
    public static DeterministicBuildMetadata AnalyzeAssembly(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();
        
        // Get module definition for MVID
        var module = metadataReader.GetModuleDefinition();
        var mvid = metadataReader.GetGuid(module.Mvid);
        
        // Get assembly definition
        var assembly = metadataReader.GetAssemblyDefinition();
        
        // Extract public key and calculate public key token
        byte[]? publicKey = null;
        byte[]? publicKeyToken = null;
        if (!assembly.PublicKey.IsNil)
        {
            publicKey = metadataReader.GetBlobBytes(assembly.PublicKey);
            publicKeyToken = CalculatePublicKeyToken(publicKey);
        }
        
        // Analyze debug directory
        var debugEntries = peReader.ReadDebugDirectory();
        var hasReproducibleEntry = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.Reproducible);
        var hasPdbChecksum = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.PdbChecksum);
        var hasEmbeddedPdb = debugEntries.Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        
        // Extract PDB checksum if present
        byte[]? pdbChecksum = null;
        string? pdbChecksumAlgorithm = null;
        var pdbChecksumEntry = debugEntries.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.PdbChecksum);
        if (pdbChecksumEntry.DataSize > 0)
        {
            // Note: Reading PDB checksum data requires unsafe code or reflection
            // For now, we just note that it exists
            pdbChecksumAlgorithm = "SHA256"; // Most common
            // We can't easily read the checksum bytes without unsafe code
        }
        
        // Get PE headers for timestamp
        var peTimestamp = peReader.PEHeaders.CoffHeader.TimeDateStamp;
        
        return new DeterministicBuildMetadata
        {
            Mvid = mvid,
            PublicKey = publicKey,
            PublicKeyToken = publicKeyToken,
            HasReproducibleEntry = hasReproducibleEntry,
            HasPdbChecksum = hasPdbChecksum,
            HasEmbeddedPdb = hasEmbeddedPdb,
            PdbChecksum = pdbChecksum,
            PdbChecksumAlgorithm = pdbChecksumAlgorithm,
            PeTimestamp = peTimestamp,
            DebugEntryCount = debugEntries.Length
        };
    }
    
    private static byte[] CalculatePublicKeyToken(byte[] publicKey)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);
        
        // Public key token is last 8 bytes of SHA1 hash, reversed
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            token[i] = hash[hash.Length - 1 - i];
        }
        
        return token;
    }
    
    private static string ReadPdbChecksumAlgorithm(byte[] data)
    {
        // PDB checksum format: algorithm name (null-terminated string) followed by checksum bytes
        var nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex > 0)
        {
            return System.Text.Encoding.UTF8.GetString(data, 0, nullIndex);
        }
        return "Unknown";
    }
    
    private static byte[] ReadPdbChecksum(byte[] data)
    {
        // Skip algorithm name
        var nullIndex = Array.IndexOf(data, (byte)0);
        if (nullIndex > 0 && nullIndex < data.Length - 1)
        {
            var checksumLength = data.Length - nullIndex - 1;
            var checksum = new byte[checksumLength];
            Array.Copy(data, nullIndex + 1, checksum, 0, checksumLength);
            return checksum;
        }
        return Array.Empty<byte>();
    }
    
    public static void DisplayAnalysis(string assemblyPath, DeterministicBuildMetadata metadata)
    {
        AnsiConsole.MarkupLine($"\n[cyan]Deterministic Build Analysis:[/] {Path.GetFileName(assemblyPath)}");
        AnsiConsole.MarkupLine($"  MVID: [yellow]{metadata.Mvid}[/]");
        AnsiConsole.MarkupLine($"  PE Timestamp: [yellow]{metadata.PeTimestamp}[/]");
        AnsiConsole.MarkupLine($"  Reproducible Entry: [yellow]{metadata.HasReproducibleEntry}[/]");
        AnsiConsole.MarkupLine($"  PDB Checksum: [yellow]{metadata.HasPdbChecksum}[/]");
        AnsiConsole.MarkupLine($"  Embedded PDB: [yellow]{metadata.HasEmbeddedPdb}[/]");
        AnsiConsole.MarkupLine($"  Debug Entries: [yellow]{metadata.DebugEntryCount}[/]");
        
        if (metadata.PublicKeyToken != null)
        {
            var tokenHex = BitConverter.ToString(metadata.PublicKeyToken).Replace("-", "");
            AnsiConsole.MarkupLine($"  Public Key Token: [yellow]{tokenHex}[/]");
        }
        
        if (metadata.PdbChecksum != null && metadata.PdbChecksumAlgorithm != null)
        {
            var checksumHex = BitConverter.ToString(metadata.PdbChecksum).Replace("-", "");
            AnsiConsole.MarkupLine($"  PDB Checksum Algorithm: [yellow]{metadata.PdbChecksumAlgorithm}[/]");
            AnsiConsole.MarkupLine($"  PDB Checksum: [yellow]{checksumHex}[/]");
        }
    }
}

/// <summary>
/// Metadata required for deterministic build reproduction.
/// </summary>
public class DeterministicBuildMetadata
{
    public Guid Mvid { get; set; }
    public byte[]? PublicKey { get; set; }
    public byte[]? PublicKeyToken { get; set; }
    public bool HasReproducibleEntry { get; set; }
    public bool HasPdbChecksum { get; set; }
    public bool HasEmbeddedPdb { get; set; }
    public byte[]? PdbChecksum { get; set; }
    public string? PdbChecksumAlgorithm { get; set; }
    public int PeTimestamp { get; set; }
    public int DebugEntryCount { get; set; }
}
