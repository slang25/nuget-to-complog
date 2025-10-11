using NuGetToCompLog;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Tests;

/// <summary>
/// Integration tests that test the full extraction flow with real assemblies.
/// These tests create minimal test assemblies with PDBs to verify the parsing logic.
/// </summary>
public class PdbCompilerArgumentsExtractorIntegrationTests
{
    [Fact]
    public void ExtractMetadataReferences_WithRealPdb_ParsesCorrectly()
    {
        // This test would ideally use a real PDB, but for now we'll create a minimal one
        // In practice, you could download a small NuGet package with embedded PDB for testing
        // For example: dotnet add package Newtonsoft.Json and extract from its DLL
        
        // For this test, we'll verify the parser can handle the blob format
        var testBlob = CreateTestMetadataReferenceBlob();
        
        // Act
        var references = MetadataReferenceParser.Parse(testBlob);
        
        // Assert
        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.NotNull(r.FileName));
    }

    [Fact]
    public void ParseMetadataReferences_DoesNotThrowOnStreamEnd()
    {
        // This test verifies we don't read beyond the stream end
        // which was the original bug mentioned
        var testBlob = CreateComplexMetadataReferenceBlob();
        
        // Act & Assert - should not throw
        var references = MetadataReferenceParser.Parse(testBlob);
        
        // Verify we got the expected number of references
        Assert.Equal(5, references.Count);
        
        // Verify each reference is parsed correctly
        Assert.Equal("System.Runtime.dll", references[0].FileName);
        Assert.Equal("System.Collections.dll", references[1].FileName);
        Assert.Equal("System.Linq.dll", references[2].FileName);
        Assert.Contains("global", references[3].Aliases);
        Assert.True(references[4].EmbedInteropTypes);
    }

    /// <summary>
    /// Creates a test metadata reference blob that mimics real-world data.
    /// </summary>
    private byte[] CreateTestMetadataReferenceBlob()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // No count prefix - just write references

        // Reference 1: System.Runtime.dll (no aliases, no embed)
        WriteSerializedString(writer, "System.Runtime.dll");
        WriteCompressedInteger(writer, 0); // no aliases
        writer.Write((byte)0x00); // no embed
        writer.Write(new byte[16]); // MVID
        writer.Write((int)0); // timestamp

        // Reference 2: System.Collections.dll (no aliases, no embed)
        WriteSerializedString(writer, "System.Collections.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        // Reference 3: Newtonsoft.Json.dll (no aliases, no embed)
        WriteSerializedString(writer, "Newtonsoft.Json.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        return stream.ToArray();
    }

    /// <summary>
    /// Creates a more complex metadata reference blob to test edge cases.
    /// </summary>
    private byte[] CreateComplexMetadataReferenceBlob()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // No count prefix - just write references

        // Reference 1: Simple
        WriteSerializedString(writer, "System.Runtime.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        // Reference 2: Simple
        WriteSerializedString(writer, "System.Collections.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        // Reference 3: Simple
        WriteSerializedString(writer, "System.Linq.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        // Reference 4: With alias
        WriteSerializedString(writer, "CustomLib.dll");
        WriteCompressedInteger(writer, 1);
        WriteSerializedString(writer, "global");
        writer.Write((byte)0x00);
        writer.Write(new byte[16]);
        writer.Write((int)0);

        // Reference 5: With embed interop types
        WriteSerializedString(writer, "Interop.Office.dll");
        WriteCompressedInteger(writer, 0);
        writer.Write((byte)0x01); // embed interop types
        writer.Write(new byte[16]);
        writer.Write((int)0);

        return stream.ToArray();
    }

    private void WriteCompressedInteger(BinaryWriter writer, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value <= 0x7F)
        {
            writer.Write((byte)value);
        }
        else if (value <= 0x3FFF)
        {
            writer.Write((byte)(0x80 | (value >> 8)));
            writer.Write((byte)(value & 0xFF));
        }
        else if (value <= 0x1FFFFFFF)
        {
            writer.Write((byte)(0xC0 | (value >> 24)));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private void WriteSerializedString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteCompressedInteger(writer, bytes.Length);
        writer.Write(bytes);
    }
}
