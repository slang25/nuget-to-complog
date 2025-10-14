namespace NuGetToCompLog.Domain;

/// <summary>
/// Represents an embedded resource from an assembly.
/// </summary>
public record EmbeddedResourceInfo(
    string Name,
    byte[] Content,
    long Size)
{
    public EmbeddedResourceInfo() : this(
        string.Empty,
        Array.Empty<byte>(),
        0)
    {
    }
}
