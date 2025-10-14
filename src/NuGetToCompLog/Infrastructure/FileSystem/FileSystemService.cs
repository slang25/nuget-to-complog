using System.IO.Compression;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Infrastructure.FileSystem;

/// <summary>
/// Default implementation of file system operations.
/// </summary>
public class FileSystemService : IFileSystemService
{
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetFiles(path, searchPattern, searchOption);
    }

    public string[] GetDirectories(string path)
    {
        return Directory.GetDirectories(path);
    }

    public void CopyFile(string sourceFileName, string destFileName, bool overwrite)
    {
        File.Copy(sourceFileName, destFileName, overwrite);
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    public async Task<string[]> ReadAllLinesAsync(string path, CancellationToken cancellationToken = default)
    {
        return await File.ReadAllLinesAsync(path, cancellationToken);
    }

    public async Task WriteAllLinesAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken = default)
    {
        await File.WriteAllLinesAsync(path, lines, cancellationToken);
    }

    public async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    public async Task ExtractZipAsync(string zipPath, string destinationPath, bool overwrite)
    {
        await Task.Run(() =>
        {
            ZipFile.ExtractToDirectory(zipPath, destinationPath, overwrite);
        });
    }

    public string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "nuget-to-complog", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    public long GetFileSize(string path)
    {
        var fileInfo = new FileInfo(path);
        return fileInfo.Length;
    }
}
