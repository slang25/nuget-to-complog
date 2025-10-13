namespace NuGetToCompLog.Abstractions;

/// <summary>
/// Service for file system operations.
/// </summary>
public interface IFileSystemService
{
    /// <summary>
    /// Creates a directory if it doesn't exist.
    /// </summary>
    void CreateDirectory(string path);
    
    /// <summary>
    /// Checks if a directory exists.
    /// </summary>
    bool DirectoryExists(string path);
    
    /// <summary>
    /// Checks if a file exists.
    /// </summary>
    bool FileExists(string path);
    
    /// <summary>
    /// Gets all files in a directory matching a pattern.
    /// </summary>
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);
    
    /// <summary>
    /// Gets all subdirectories in a directory.
    /// </summary>
    string[] GetDirectories(string path);
    
    /// <summary>
    /// Copies a file to a destination.
    /// </summary>
    void CopyFile(string sourceFileName, string destFileName, bool overwrite);
    
    /// <summary>
    /// Reads all text from a file.
    /// </summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Writes all text to a file.
    /// </summary>
    Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Reads all lines from a file.
    /// </summary>
    Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Writes all lines to a file.
    /// </summary>
    Task WriteAllLinesAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Extracts a zip archive to a directory.
    /// </summary>
    Task ExtractZipAsync(string zipPath, string destinationPath, bool overwrite);
    
    /// <summary>
    /// Creates a temporary directory and returns its path.
    /// </summary>
    string CreateTempDirectory();
    
    /// <summary>
    /// Gets the file size in bytes.
    /// </summary>
    long GetFileSize(string path);
}
