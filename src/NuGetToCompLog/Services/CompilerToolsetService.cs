using System.IO.Compression;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services;

/// <summary>
/// Downloads the exact Roslyn compiler recorded in a package's PDB, shipped as the
/// Microsoft.Net.Compilers.Toolset package. Release builds (stable / "-N.final") are on
/// nuget.org; the per-build versions SDKs ship (e.g. "4.12.0-3.24570.6") are only published
/// to the public dnceng dotnet-tools feed, which has limited retention for older builds.
/// </summary>
public class CompilerToolsetService
{
    private const string PackageId = "Microsoft.Net.Compilers.Toolset";

    private static readonly string[] Feeds =
    [
        "https://api.nuget.org/v3/index.json",
        "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json",
    ];

    private readonly IConsoleWriter _console;

    public CompilerToolsetService(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Returns the path to csc.dll for the PDB-recorded compiler version
    /// (e.g. "4.12.0-3.24570.6+sha"), downloading and caching the toolset package on first
    /// use. Returns null when no feed has that version.
    /// </summary>
    public async Task<string?> TryGetCscAsync(string compilerVersion, CancellationToken cancellationToken)
    {
        var version = compilerVersion.Split('+')[0];
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nuget-to-complog",
            "compilers",
            version);
        var cscPath = Path.Combine(cacheDir, "tasks", "netcore", "bincore", "csc.dll");
        if (File.Exists(cscPath))
        {
            return cscPath;
        }

        if (!NuGetVersion.TryParse(version, out var nugetVersion))
        {
            return null;
        }

        foreach (var feed in Feeds)
        {
            try
            {
                var repository = Repository.Factory.GetCoreV3(feed);
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                using var cache = new SourceCacheContext();
                using var stream = new MemoryStream();
                var found = await resource.CopyNupkgToStreamAsync(
                    PackageId, nugetVersion, stream, cache, NullLogger.Instance, cancellationToken);
                if (!found)
                {
                    continue;
                }

                stream.Position = 0;
                ExtractNetCoreCompiler(stream, cacheDir, cscPath);
                if (File.Exists(cscPath))
                {
                    _console.MarkupLine($"  [green]✓[/] Downloaded {PackageId} {version} from {new Uri(feed).Host}");
                    return cscPath;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user cancelled - falling through to the next feed (and ultimately to a
                // null result) would let verify carry on with a different compiler than the
                // PDB recorded. A feed's own timeout still counts as a feed failure below.
                throw;
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"  [dim]{new Uri(feed).Host}: {ex.Message.Replace("[", "[[").Replace("]", "]]")}[/]");
            }
        }

        return null;
    }

    private static void ExtractNetCoreCompiler(Stream nupkg, string cacheDir, string cscPath)
    {
        // Extract to a caller-unique sibling directory and move it into place, so a cancelled
        // download can never leave a half-populated cache entry that later runs would trust.
        // The name must be unique: `verify --fetch-compiler` runs concurrently across packages,
        // and a shared ".tmp" path would let one process delete another's in-progress
        // extraction. The install is a single Move onto a never-deleted destination, so a
        // process that loses the race keeps reading a cache entry that stays intact.
        var tempDir = $"{cacheDir}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = new ZipArchive(nupkg, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith("tasks/netcore/", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var destination = Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            if (!File.Exists(Path.Combine(tempDir, "tasks", "netcore", "bincore", "csc.dll")))
            {
                // Not the layout we need - don't publish it, so an installed cache entry always
                // means a usable csc.dll and "destination exists" always means "already valid".
                return;
            }

            if (Directory.Exists(cacheDir))
            {
                // Another process installed this version while we were extracting; its cache
                // entry is as good as ours, so discard the extraction rather than replace it.
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
            try
            {
                Directory.Move(tempDir, cacheDir);
            }
            catch (IOException) when (Directory.Exists(cacheDir))
            {
                // Lost the race between the check above and the move; the winner's entry stands.
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
