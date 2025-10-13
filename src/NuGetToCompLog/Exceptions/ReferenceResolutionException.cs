namespace NuGetToCompLog.Exceptions;

/// <summary>
/// Exception thrown when reference assembly resolution fails.
/// </summary>
public class ReferenceResolutionException : Exception
{
    public string? AssemblyName { get; }
    
    public ReferenceResolutionException(string message)
        : base(message)
    {
    }
    
    public ReferenceResolutionException(string message, string? assemblyName)
        : base(message)
    {
        AssemblyName = assemblyName;
    }
    
    public ReferenceResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
    
    public ReferenceResolutionException(string message, string? assemblyName, Exception innerException)
        : base(message, innerException)
    {
        AssemblyName = assemblyName;
    }
}
