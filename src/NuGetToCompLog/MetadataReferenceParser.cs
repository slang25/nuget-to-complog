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
///   - File name (serialized string: compressed length, then UTF-8 bytes)
///   - Extern alias count (compressed integer)
///   - For each alias:
///     - Alias name (serialized string)
///   - Properties byte:
///     - Bit 0: EmbedInteropTypes
///     - Bits 1-7: Reserved (must be 0)
///   - MVID (16 bytes - Module Version ID GUID, might be all zeros)
///   - Timestamp (4 bytes - might be zero or -1)
/// </summary>
public static class MetadataReferenceParser
{
    public static List<MetadataReference> Parse(byte[] blob)
    {
        using var stream = new MemoryStream(blob);
        using var reader = new BinaryReader(stream);

        var references = new List<MetadataReference>();
        
        // Read references until we reach the end of the stream
        while (stream.Position < stream.Length)
        {
            try
            {
                // Read file name (serialized string: compressed length + UTF-8 bytes)
                string fileName = ReadSerializedString(reader);
                
                // Read extern aliases count (compressed integer)
                int aliasCount = ReadCompressedInteger(reader);
                var aliases = new List<string>();
                for (int j = 0; j < aliasCount; j++)
                {
                    string alias = ReadSerializedString(reader);
                    aliases.Add(alias);
                }
                
                // Read properties byte
                byte properties = reader.ReadByte();
                bool embedInteropTypes = (properties & 0x01) != 0;
                
                // Skip MVID (16 bytes) and timestamp (4 bytes)
                // These are used internally by the compiler but not needed for our purposes
                if (stream.Position + 20 <= stream.Length)
                {
                    reader.ReadBytes(20); // MVID (16) + timestamp (4)
                }
                
                references.Add(new MetadataReference(fileName, aliases, embedInteropTypes));
            }
            catch (EndOfStreamException)
            {
                // If we can't read a complete reference, stop
                // This might indicate the blob is malformed or we misunderstood the format
                break;
            }
        }
        
        return references;
    }
    
    /// <summary>
    /// Reads a compressed integer from the stream.
    /// Uses the same format as ECMA-335 metadata compressed integers.
    /// </summary>
    private static int ReadCompressedInteger(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        
        // Single-byte encoding: 0xxx xxxx
        if ((b & 0x80) == 0)
        {
            return b;
        }
        
        // Two-byte encoding: 10xx xxxx yyyy yyyy
        if ((b & 0xC0) == 0x80)
        {
            return ((b & 0x3F) << 8) | reader.ReadByte();
        }
        
        // Four-byte encoding: 110x xxxx yyyy yyyy zzzz zzzz wwww wwww
        if ((b & 0xE0) == 0xC0)
        {
            return ((b & 0x1F) << 24) 
                | (reader.ReadByte() << 16) 
                | (reader.ReadByte() << 8) 
                | reader.ReadByte();
        }
        
        throw new InvalidDataException($"Invalid compressed integer format: 0x{b:X2}");
    }
    
    /// <summary>
    /// Reads a serialized string from the stream.
    /// Format: compressed integer length, followed by UTF-8 bytes.
    /// </summary>
    private static string ReadSerializedString(BinaryReader reader)
    {
        int length = ReadCompressedInteger(reader);
        
        if (length == 0)
        {
            return string.Empty;
        }
        
        byte[] bytes = reader.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
