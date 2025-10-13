namespace NuGetToCompLog.Exceptions;

/// <summary>
/// Exception thrown when CompLog file creation fails.
/// </summary>
public class CompLogCreationException : Exception
{
    public string? PackageId { get; }
    public string? Version { get; }
    
    public CompLogCreationException(string message)
        : base(message)
    {
    }
    
    public CompLogCreationException(string message, string? packageId, string? version)
        : base(message)
    {
        PackageId = packageId;
        Version = version;
    }
    
    public CompLogCreationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
    
    public CompLogCreationException(string message, string? packageId, string? version, Exception innerException)
        : base(message, innerException)
    {
        PackageId = packageId;
        Version = version;
    }
}
