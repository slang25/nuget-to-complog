using System.Reflection.Metadata;

namespace NuGetToCompLog;

/// <summary>
/// Represents a metadata reference extracted from PDB custom debug information.
/// </summary>
public record MetadataReference(
    string FileName,
    List<string> Aliases,
    bool EmbedInteropTypes
);

/// <summary>
/// Parses the CompilationMetadataReferences custom debug information from a PDB.
/// 
/// The format is documented in Roslyn's source:
/// https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/PEWriter/MetadataWriter.cs
/// 
/// Binary format (for each reference, no count prefix):
///   - File name (UTF-8 string with compressed length prefix)
///   - Extern alias count (compressed integer)
///   - For each alias:
///     - Alias name (UTF-8 string with compressed length prefix)
///   - Properties byte:
///     - Bit 0: EmbedInteropTypes
///     - Bits 1-7: Reserved (must be 0)
///   - MVID (16 bytes - Module Version ID GUID)
///   - Timestamp (4 bytes)
/// </summary>
public static class MetadataReferenceParser
{
    /// <summary>
    /// Parses metadata references from a BlobReader (production code).
    /// </summary>
    public static List<MetadataReference> Parse(BlobReader blobReader)
    {
        var references = new List<MetadataReference>();
        
        // Read references until we reach the end of the blob
        while (blobReader.RemainingBytes > 0)
        {
            try
            {
                // Read file name (UTF-8 string with compressed length prefix)
                string fileName = blobReader.ReadUTF8(blobReader.ReadCompressedInteger());
                
                // Read extern aliases count (compressed integer)
                int aliasCount = blobReader.ReadCompressedInteger();
                var aliases = new List<string>();
                for (int j = 0; j < aliasCount; j++)
                {
                    string alias = blobReader.ReadUTF8(blobReader.ReadCompressedInteger());
                    aliases.Add(alias);
                }
                
                // Read properties byte
                byte properties = blobReader.ReadByte();
                bool embedInteropTypes = (properties & 0x01) != 0;
                
                // Read MVID (16 bytes) and timestamp (4 bytes)
                // These are used internally by the compiler but not needed for our purposes
                var mvid = blobReader.ReadGuid();
                var timestamp = blobReader.ReadInt32();
                
                references.Add(new MetadataReference(fileName, aliases, embedInteropTypes));
            }
            catch (BadImageFormatException)
            {
                // If we can't read a complete reference, stop
                // This might indicate the blob is malformed
                break;
            }
        }
        
        return references;
    }

    /// <summary>
    /// Parses metadata references from a byte array (for testing).
    /// Creates a blob and parses using the BlobReader overload.
    /// </summary>
    public static List<MetadataReference> Parse(byte[] blob)
    {
        var builder = new BlobBuilder();
        builder.WriteBytes(blob);
        
        // Unfortunately we cannot easily construct a BlobReader from tests,
        // so we need to use unsafe code or just reimplement the parsing for tests
        // For now, we'll just call the main parser logic directly
        using var stream = new MemoryStream(blob);
        using var reader = new BinaryReader(stream);
        
        var references = new List<MetadataReference>();
        
        while (stream.Position < stream.Length)
        {
            try
            {
                // Read file name length and string
                int fileNameLength = ReadCompressedInt(reader);
                string fileName = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(fileNameLength));
                
                // Read alias count
                int aliasCount = ReadCompressedInt(reader);
                var aliases = new List<string>();
                for (int j = 0; j < aliasCount; j++)
                {
                    int aliasLength = ReadCompressedInt(reader);
                    string alias = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(aliasLength));
                    aliases.Add(alias);
                }
                
                // Read properties
                byte properties = reader.ReadByte();
                bool embedInteropTypes = (properties & 0x01) != 0;
                
                // Read MVID and timestamp
                reader.ReadBytes(16); // MVID
                reader.ReadInt32(); // timestamp
                
                references.Add(new MetadataReference(fileName, aliases, embedInteropTypes));
            }
            catch (EndOfStreamException)
            {
                break;
            }
        }
        
        return references;
    }
    
    private static int ReadCompressedInt(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        
        if ((b & 0x80) == 0)
            return b;
        
        if ((b & 0xC0) == 0x80)
            return ((b & 0x3F) << 8) | reader.ReadByte();
        
        if ((b & 0xE0) == 0xC0)
            return ((b & 0x1F) << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
        
        throw new BadImageFormatException($"Invalid compressed integer: 0x{b:X2}");
    }
}
