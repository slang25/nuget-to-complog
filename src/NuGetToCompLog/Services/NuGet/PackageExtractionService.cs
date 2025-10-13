using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.NuGet;

/// <summary>
/// Service for extracting NuGet packages.
/// </summary>
public class PackageExtractionService
{
    private readonly IFileSystemService _fileSystem;

    public PackageExtractionService(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Extracts a package to a destination directory.
    /// </summary>
    public async Task ExtractPackageAsync(string packagePath, string extractPath)
    {
        _fileSystem.CreateDirectory(extractPath);
        await _fileSystem.ExtractZipAsync(packagePath, extractPath, overwrite: true);
    }

    /// <summary>
    /// Finds all assemblies in an extracted package.
    /// </summary>
    public List<string> FindAssemblies(string extractPath)
    {
        var assemblies = new List<string>();

        // Look in lib folder
        var libPath = Path.Combine(extractPath, "lib");
        if (_fileSystem.DirectoryExists(libPath))
        {
            assemblies.AddRange(_fileSystem.GetFiles(libPath, "*.dll", SearchOption.AllDirectories));
        }

        // Look in ref folder
        var refPath = Path.Combine(extractPath, "ref");
        if (_fileSystem.DirectoryExists(refPath))
        {
            assemblies.AddRange(_fileSystem.GetFiles(refPath, "*.dll", SearchOption.AllDirectories));
        }

        return assemblies;
    }

    /// <summary>
    /// Finds PDB files in an extracted package or symbols package.
    /// </summary>
    public List<string> FindPdbFiles(string extractPath)
    {
        if (!_fileSystem.DirectoryExists(extractPath))
        {
            return new List<string>();
        }

        return _fileSystem.GetFiles(extractPath, "*.pdb", SearchOption.AllDirectories).ToList();
    }
}
