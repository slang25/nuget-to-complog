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
/// https://github.com/dotnet/roslyn/blob/main/docs/specs/PortablePdb-Metadata.md#compilationmetadatareferences-custom-debug-information
/// 
/// Binary format:
/// - Count (compressed integer)
/// - For each reference:
///   - File name (compressed string - length as compressed int, then UTF-8 bytes)
///   - Alias count (compressed integer)
///   - For each alias:
///     - Alias name (compressed string)
///   - Properties byte:
///     - Bit 0: EmbedInteropTypes
///     - Bits 1-7: Reserved (must be 0)
/// </summary>
public static class MetadataReferenceParser
{
    public static List<MetadataReference> Parse(byte[] blob)
    {
        using var stream = new MemoryStream(blob);
        using var reader = new BinaryReader(stream);

        var references = new List<MetadataReference>();
        
        // Read the count of references (compressed integer)
        int count = ReadCompressedInteger(reader);
        
        for (int i = 0; i < count; i++)
        {
            // Read file name (compressed string)
            string fileName = ReadCompressedString(reader);
            
            // Read extern aliases count (compressed integer)
            int aliasCount = ReadCompressedInteger(reader);
            var aliases = new List<string>();
            for (int j = 0; j < aliasCount; j++)
            {
                string alias = ReadCompressedString(reader);
                aliases.Add(alias);
            }
            
            // Read properties byte
            byte properties = reader.ReadByte();
            bool embedInteropTypes = (properties & 0x01) != 0;
            
            references.Add(new MetadataReference(fileName, aliases, embedInteropTypes));
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
    /// Reads a compressed string from the stream.
    /// Format: compressed integer length, followed by UTF-8 bytes.
    /// </summary>
    private static string ReadCompressedString(BinaryReader reader)
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
