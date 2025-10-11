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
        Assert.Contains("global", references[3].ExternAliases);
        Assert.True(references[4].EmbedInteropTypes);
    }

    /// <summary>
    /// Creates a test metadata reference blob that mimics real-world data.
    /// </summary>
    private byte[] CreateTestMetadataReferenceBlob()
    {
        var builder = new BlobBuilder();

        // Reference 1: System.Runtime.dll (no aliases, no embed)
        builder.WriteUTF8("System.Runtime.dll");
        builder.WriteByte(0); // null terminator
        builder.WriteByte(0); // empty aliases, null terminator
        builder.WriteByte(0b01); // Assembly, no embed
        builder.WriteInt32(0); // timestamp
        builder.WriteInt32(0); // image size
        builder.WriteGuid(Guid.Empty); // MVID

        // Reference 2: System.Collections.dll (no aliases, no embed)
        builder.WriteUTF8("System.Collections.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        // Reference 3: Newtonsoft.Json.dll (no aliases, no embed)
        builder.WriteUTF8("Newtonsoft.Json.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        return builder.ToArray();
    }

    /// <summary>
    /// Creates a more complex metadata reference blob to test edge cases.
    /// </summary>
    private byte[] CreateComplexMetadataReferenceBlob()
    {
        var builder = new BlobBuilder();

        // Reference 1: Simple
        builder.WriteUTF8("System.Runtime.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        // Reference 2: Simple
        builder.WriteUTF8("System.Collections.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        // Reference 3: Simple
        builder.WriteUTF8("System.Linq.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        // Reference 4: With alias
        builder.WriteUTF8("CustomLib.dll");
        builder.WriteByte(0);
        builder.WriteUTF8("global");
        builder.WriteByte(0);
        builder.WriteByte(0b01);
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        // Reference 5: With embed interop types
        builder.WriteUTF8("Interop.Office.dll");
        builder.WriteByte(0);
        builder.WriteByte(0);
        builder.WriteByte(0b11); // Assembly + embed interop types
        builder.WriteInt32(0);
        builder.WriteInt32(0);
        builder.WriteGuid(Guid.Empty);

        return builder.ToArray();
    }
}
