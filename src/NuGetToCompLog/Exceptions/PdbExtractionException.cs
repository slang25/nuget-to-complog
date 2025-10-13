namespace NuGetToCompLog.Exceptions;

/// <summary>
/// Exception thrown when PDB extraction or parsing fails.
/// </summary>
public class PdbExtractionException : Exception
{
    public string? PdbPath { get; }
    
    public PdbExtractionException(string message)
        : base(message)
    {
    }
    
    public PdbExtractionException(string message, string? pdbPath)
        : base(message)
    {
        PdbPath = pdbPath;
    }
    
    public PdbExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
    
    public PdbExtractionException(string message, string? pdbPath, Exception innerException)
        : base(message, innerException)
    {
        PdbPath = pdbPath;
    }
}
