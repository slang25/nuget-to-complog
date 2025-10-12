using System.Reflection.Metadata;

namespace NuGetToCompLog;

/// <summary>
/// Represents a metadata reference extracted from PDB custom debug information.
/// </summary>
public record MetadataReference(
    string FileName,
    List<string> ExternAliases,
    bool EmbedInteropTypes,
    MetadataImageKind Kind,
    int Timestamp,
    int ImageSize,
    Guid Mvid
);

/// <summary>
/// Metadata image kind (Assembly or Module).
/// </summary>
public enum MetadataImageKind
{
    Module = 0,
    Assembly = 1
}

/// <summary>
/// Parses the CompilationMetadataReferences custom debug information from a PDB.
/// 
/// Binary format (for each reference, no count prefix):
///   - File name (null-terminated UTF-8 string)
///   - Extern aliases (null-terminated UTF-8 string, comma-separated)
///   - EmbedInteropTypes/MetadataImageKind (byte):
///     - Bit 0: MetadataImageKind (0=Module, 1=Assembly)
///     - Bit 1: EmbedInteropTypes
///   - Timestamp (4 bytes - COFF header Timestamp field)
///   - ImageSize (4 bytes - COFF header SizeOfImage field)
///   - MVID (16 bytes - Module Version ID GUID)
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
                // Read file name (null-terminated UTF-8 string)
                var terminatorIndex = blobReader.IndexOf(0);
                var fileName = blobReader.ReadUTF8(terminatorIndex);
                blobReader.ReadByte(); // Skip null terminator
                
                // Read extern aliases (null-terminated UTF-8 string, comma-separated)
                terminatorIndex = blobReader.IndexOf(0);
                var externAliasesStr = blobReader.ReadUTF8(terminatorIndex);
                blobReader.ReadByte(); // Skip null terminator
                
                var externAliases = string.IsNullOrEmpty(externAliasesStr) 
                    ? []
                    : externAliasesStr.Split(',').ToList();
                
                // Read EmbedInteropTypes/MetadataImageKind byte
                var embedInteropTypesAndKind = blobReader.ReadByte();
                var embedInteropTypes = (embedInteropTypesAndKind & 0b10) == 0b10;
                var kind = (embedInteropTypesAndKind & 0b1) == 0b1
                    ? MetadataImageKind.Assembly
                    : MetadataImageKind.Module;
                
                // Read timestamp (4 bytes - COFF header Timestamp field)
                var timestamp = blobReader.ReadInt32();
                
                // Read image size (4 bytes - COFF header SizeOfImage field)
                var imageSize = blobReader.ReadInt32();
                
                // Read MVID (16 bytes - Module Version ID GUID)
                var mvid = blobReader.ReadGuid();
                
                references.Add(new MetadataReference(
                    fileName, 
                    externAliases, 
                    embedInteropTypes, 
                    kind, 
                    timestamp, 
                    imageSize, 
                    mvid));
            }
            catch (BadImageFormatException)
            {
                // If we can't read a complete reference, stop
                break;
            }
        }
        
        return references;
    }

    /// <summary>
    /// Parses metadata references from a byte array (for testing).
    /// </summary>
    public static List<MetadataReference> Parse(byte[] blob)
    {
        using var stream = new MemoryStream(blob);
        using var reader = new BinaryReader(stream);
        
        var references = new List<MetadataReference>();
        
        while (stream.Position < stream.Length)
        {
            try
            {
                // Read file name (null-terminated string)
                var fileName = ReadNullTerminatedString(reader);
                
                // Read extern aliases (null-terminated string, comma-separated)
                var externAliasesStr = ReadNullTerminatedString(reader);
                var externAliases = string.IsNullOrEmpty(externAliasesStr)
                    ? []
                    : externAliasesStr.Split(',').ToList();
                
                // Read EmbedInteropTypes/MetadataImageKind byte
                var embedInteropTypesAndKind = reader.ReadByte();
                var embedInteropTypes = (embedInteropTypesAndKind & 0b10) == 0b10;
                var kind = (embedInteropTypesAndKind & 0b1) == 0b1
                    ? MetadataImageKind.Assembly
                    : MetadataImageKind.Module;
                
                // Read timestamp
                var timestamp = reader.ReadInt32();
                
                // Read image size
                var imageSize = reader.ReadInt32();
                
                // Read MVID
                var mvidBytes = reader.ReadBytes(16);
                var mvid = new Guid(mvidBytes);
                
                references.Add(new MetadataReference(
                    fileName,
                    externAliases,
                    embedInteropTypes,
                    kind,
                    timestamp,
                    imageSize,
                    mvid));
            }
            catch (EndOfStreamException)
            {
                break;
            }
        }
        
        return references;
    }
    
    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
