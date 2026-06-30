using System.Net.Http;
using System.Reflection.PortableExecutable;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Infrastructure.Http;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Downloads portable PDBs from public symbol servers using the Simple Symbol
/// Query Protocol (SSQP).
///
/// This recovers symbols for packages that publish to a symbol server
/// (e.g. msdl.microsoft.com / symbols.nuget.org) rather than shipping a
/// <c>.snupkg</c> on nuget.org — which is the case for most Microsoft-owned
/// packages such as <c>Microsoft.Extensions.*</c>, <c>System.Text.Json</c> and
/// <c>Azure.Core</c>.
/// </summary>
public class SymbolServerClient
{
    // Portable PDBs are indexed under a fixed "age" of 0xFFFFFFFF (the field that
    // carries the build age for classic Windows PDBs).
    private const string PortablePdbAge = "FFFFFFFF";

    // Ordered by likelihood for the packages we target. symbols.nuget.org covers
    // community packages; msdl covers Microsoft's first-party symbols.
    private static readonly string[] SymbolServers =
    {
        "https://symbols.nuget.org/download/symbols",
        "https://msdl.microsoft.com/download/symbols",
    };

    private readonly IConsoleWriter _console;
    private readonly IHttpClientFactory _httpClientFactory;

    public SymbolServerClient(IConsoleWriter console, IHttpClientFactory httpClientFactory)
    {
        _console = console;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Attempts to download the PDB for <paramref name="assemblyPath"/> from a
    /// public symbol server, saving it next to the assembly so the normal PDB
    /// discovery picks it up.
    /// </summary>
    /// <returns>The path to the downloaded PDB, or <c>null</c> if no symbol
    /// server has it (or the assembly carries no CodeView debug directory).</returns>
    public async Task<string?> TryDownloadPdbAsync(
        string assemblyPath,
        CancellationToken cancellationToken = default)
    {
        var index = TryGetSymbolIndex(assemblyPath);
        if (index == null)
        {
            return null;
        }

        var (pdbFileName, ssqpKey) = index.Value;

        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (assemblyDir == null)
        {
            return null;
        }

        var destinationPath = Path.Combine(assemblyDir, pdbFileName);

        var httpClient = _httpClientFactory.CreateClient(HttpClientNames.SymbolServer);

        foreach (var server in SymbolServers)
        {
            var url = $"{server}/{ssqpKey}";
            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using (var fileStream = File.Create(destinationPath))
                {
                    await response.Content.CopyToAsync(fileStream, cancellationToken);
                }

                if (!IsPortablePdb(destinationPath))
                {
                    // Some servers answer 200 with an error/HTML body; don't keep junk.
                    File.Delete(destinationPath);
                    continue;
                }

                var host = new Uri(server).Host;
                _console.MarkupLine($"  [green]✓ Downloaded PDB from symbol server:[/] [dim]{host}[/]");
                return destinationPath;
            }
            catch
            {
                // Try the next server.
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the SSQP lookup key for an assembly's portable PDB from its
    /// CodeView (RSDS) debug directory entry. The key has the shape
    /// <c>{pdb}/{guid:N}{FFFFFFFF}/{pdb}</c>.
    /// </summary>
    private static (string PdbFileName, string SsqpKey)? TryGetSymbolIndex(string assemblyPath)
    {
        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(peStream);

            var codeView = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.CodeView);

            if (codeView.DataSize == 0)
            {
                return null;
            }

            var data = peReader.ReadCodeViewDebugDirectoryData(codeView);

            // The CodeView path may be a full build-machine path; we only want the
            // file name for both the key and the saved file.
            var pdbFileName = Path.GetFileName(data.Path.Replace('\\', '/'));
            if (string.IsNullOrEmpty(pdbFileName))
            {
                return null;
            }

            var signature = data.Guid.ToString("N").ToUpperInvariant();
            var ssqpKey = $"{pdbFileName}/{signature}{PortablePdbAge}/{pdbFileName}";

            return (pdbFileName, ssqpKey);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies a downloaded file is a portable PDB (magic bytes "BSJB").
    /// </summary>
    private static bool IsPortablePdb(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (stream.Read(magic) != magic.Length)
            {
                return false;
            }

            return magic[0] == (byte)'B'
                && magic[1] == (byte)'S'
                && magic[2] == (byte)'J'
                && magic[3] == (byte)'B';
        }
        catch
        {
            return false;
        }
    }
}
