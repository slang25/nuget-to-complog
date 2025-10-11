using System.Reflection.Metadata;
using NuGetToCompLog;

namespace NuGetToCompLog.Tests;

public class MetadataReferenceParserTests
{
    [Fact]
    public void Parse_SingleReferenceNoAliases_ReturnsCorrectReference()
    {
        // Arrange - Create a simple metadata reference blob
        // Format: count (1), filename length, filename, alias count (0), properties (0)
        var blob = CreateMetadataReferenceBlob(new[]
        {
            ("System.Runtime.dll", Array.Empty<string>(), false)
        });

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Single(references);
        Assert.Equal("System.Runtime.dll", references[0].FileName);
        Assert.Empty(references[0].ExternAliases);
        Assert.False(references[0].EmbedInteropTypes);
        Assert.Equal(MetadataImageKind.Assembly, references[0].Kind);
    }

    [Fact]
    public void Parse_MultipleReferences_ReturnsAllReferences()
    {
        // Arrange
        var blob = CreateMetadataReferenceBlob(new[]
        {
            ("System.Runtime.dll", Array.Empty<string>(), false),
            ("System.Collections.dll", Array.Empty<string>(), false),
            ("Newtonsoft.Json.dll", Array.Empty<string>(), false)
        });

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Equal(3, references.Count);
        Assert.Equal("System.Runtime.dll", references[0].FileName);
        Assert.Equal("System.Collections.dll", references[1].FileName);
        Assert.Equal("Newtonsoft.Json.dll", references[2].FileName);
    }

    [Fact]
    public void Parse_ReferenceWithAlias_ReturnsAliases()
    {
        // Arrange
        var blob = CreateMetadataReferenceBlob(new[]
        {
            ("System.Runtime.dll", new[] { "global" }, false),
            ("MyLib.dll", new[] { "mylib", "lib2" }, false)
        });

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Equal(2, references.Count);
        Assert.Single(references[0].ExternAliases);
        Assert.Equal("global", references[0].ExternAliases[0]);
        Assert.Equal(2, references[1].ExternAliases.Count);
        Assert.Equal("mylib", references[1].ExternAliases[0]);
        Assert.Equal("lib2", references[1].ExternAliases[1]);
    }

    [Fact]
    public void Parse_ReferenceWithEmbedInteropTypes_ReturnsCorrectProperty()
    {
        // Arrange
        var blob = CreateMetadataReferenceBlob(new[]
        {
            ("System.Runtime.dll", Array.Empty<string>(), false),
            ("Interop.dll", Array.Empty<string>(), true)
        });

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Equal(2, references.Count);
        Assert.False(references[0].EmbedInteropTypes);
        Assert.True(references[1].EmbedInteropTypes);
    }

    [Fact]
    public void Parse_LongFileName_HandlesCompressedLength()
    {
        // Arrange - Use a very long file path that requires multi-byte length encoding
        var longPath = "/very/long/path/to/some/assembly/that/has/a/really/long/name/" + 
                       new string('x', 200) + ".dll";
        var blob = CreateMetadataReferenceBlob(new[]
        {
            (longPath, Array.Empty<string>(), false)
        });

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Single(references);
        Assert.Equal(longPath, references[0].FileName);
    }

    [Fact]
    public void Parse_ManyReferences_HandlesLargeCount()
    {
        // Arrange - Create a blob with many references
        var referenceData = Enumerable.Range(1, 100)
            .Select(i => ($"Assembly{i}.dll", Array.Empty<string>(), false))
            .ToArray();
        var blob = CreateMetadataReferenceBlob(referenceData);

        // Act
        var references = MetadataReferenceParser.Parse(blob);

        // Assert
        Assert.Equal(100, references.Count);
        Assert.Equal("Assembly1.dll", references[0].FileName);
        Assert.Equal("Assembly100.dll", references[99].FileName);
    }

    /// <summary>
    /// Helper method to create a properly formatted metadata reference blob using BlobBuilder.
    /// </summary>
    private byte[] CreateMetadataReferenceBlob((string fileName, string[] aliases, bool embedInteropTypes)[] references)
    {
        var builder = new BlobBuilder();

        foreach (var (fileName, aliases, embedInteropTypes) in references)
        {
            // Write file name (null-terminated UTF-8 string)
            builder.WriteUTF8(fileName);
            builder.WriteByte(0); // null terminator

            // Write extern aliases (null-terminated UTF-8 string, comma-separated)
            var aliasesStr = aliases.Length > 0 ? string.Join(",", aliases) : "";
            builder.WriteUTF8(aliasesStr);
            builder.WriteByte(0); // null terminator

            // Write EmbedInteropTypes/MetadataImageKind byte
            // Bit 0: Kind (0=Module, 1=Assembly)
            // Bit 1: EmbedInteropTypes
            byte kindAndEmbed = 0b01; // Assembly by default
            if (embedInteropTypes)
                kindAndEmbed |= 0b10;
            builder.WriteByte(kindAndEmbed);
            
            // Write timestamp (4 bytes)
            builder.WriteInt32(0);
            
            // Write image size (4 bytes)
            builder.WriteInt32(0);
            
            // Write MVID (16 bytes)
            builder.WriteGuid(Guid.Empty);
        }

        return builder.ToArray();
    }
}
