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
        Assert.Empty(references[0].Aliases);
        Assert.False(references[0].EmbedInteropTypes);
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
        Assert.Single(references[0].Aliases);
        Assert.Equal("global", references[0].Aliases[0]);
        Assert.Equal(2, references[1].Aliases.Count);
        Assert.Equal("mylib", references[1].Aliases[0]);
        Assert.Equal("lib2", references[1].Aliases[1]);
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
    /// Helper method to create a properly formatted metadata reference blob.
    /// </summary>
    private byte[] CreateMetadataReferenceBlob((string fileName, string[] aliases, bool embedInteropTypes)[] references)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Write count (compressed integer)
        WriteCompressedInteger(writer, references.Length);

        foreach (var (fileName, aliases, embedInteropTypes) in references)
        {
            // Write file name (compressed string)
            WriteCompressedString(writer, fileName);

            // Write alias count and aliases
            WriteCompressedInteger(writer, aliases.Length);
            foreach (var alias in aliases)
            {
                WriteCompressedString(writer, alias);
            }

            // Write properties byte
            byte properties = embedInteropTypes ? (byte)0x01 : (byte)0x00;
            writer.Write(properties);
        }

        return stream.ToArray();
    }

    private void WriteCompressedInteger(BinaryWriter writer, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        // Single-byte encoding: 0-127
        if (value <= 0x7F)
        {
            writer.Write((byte)value);
        }
        // Two-byte encoding: 128-16383
        else if (value <= 0x3FFF)
        {
            writer.Write((byte)(0x80 | (value >> 8)));
            writer.Write((byte)(value & 0xFF));
        }
        // Four-byte encoding: 16384-536870911
        else if (value <= 0x1FFFFFFF)
        {
            writer.Write((byte)(0xC0 | (value >> 24)));
            writer.Write((byte)((value >> 16) & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)(value & 0xFF));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value too large for compressed integer");
        }
    }

    private void WriteCompressedString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteCompressedInteger(writer, bytes.Length);
        writer.Write(bytes);
    }
}
