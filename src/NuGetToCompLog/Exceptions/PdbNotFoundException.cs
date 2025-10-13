namespace NuGetToCompLog.Exceptions;

/// <summary>
/// Exception thrown when a PDB file cannot be found for an assembly.
/// </summary>
public class PdbNotFoundException : Exception
{
    public string AssemblyPath { get; }
    
    public PdbNotFoundException(string assemblyPath)
        : base($"PDB file not found for assembly: {assemblyPath}")
    {
        AssemblyPath = assemblyPath;
    }
    
    public PdbNotFoundException(string assemblyPath, string message)
        : base(message)
    {
        AssemblyPath = assemblyPath;
    }
    
    public PdbNotFoundException(string assemblyPath, string message, Exception innerException)
        : base(message, innerException)
    {
        AssemblyPath = assemblyPath;
    }
}
