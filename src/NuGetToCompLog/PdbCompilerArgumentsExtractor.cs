using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO.Compression;
using Spectre.Console;

namespace NuGetToCompLog;

/// <summary>
/// Extracts compiler arguments and compilation metadata from portable PDB files.
/// Supports both embedded PDBs (in assemblies) and external PDB files (.pdb or from .snupkg).
/// 
/// The extraction process reads custom debug information from the PDB that contains:
/// - Compiler command-line arguments (options, defines, language version, etc.)
/// - Metadata references (all assemblies referenced during compilation)
/// - Source file listings (original source file paths)
/// - Source Link mappings (URLs to source code in version control)
/// - Embedded source code (when available)
/// 
/// This information is essential for reconstructing a Roslyn compilation workspace
/// and creating a portable CompLog file.
/// </summary>
public class PdbCompilerArgumentsExtractor
{
    public async Task ExtractCompilerArgumentsAsync(string assemblyPath, string workingDirectory)
    {
        // Try three approaches to find the PDB:
        // 1. Embedded PDB in the assembly
        // 2. External PDB file next to the assembly
        // 3. External PDB in symbols package (download if necessary)

        string? pdbPath = null;
        string? pdbFileName = null;

        // Check for embedded PDB
        using (var peStream = File.OpenRead(assemblyPath))
        using (var peReader = new PEReader(peStream))
        {
            var embeddedPdb = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

            if (embeddedPdb.DataSize > 0)
            {
                AnsiConsole.MarkupLine("  [green]✓ Found embedded PDB[/]");
                await ExtractFromEmbeddedPdbAsync(peReader);
                return;
            }

            // Check for CodeView entry which points to external PDB
            var codeView = peReader.ReadDebugDirectory()
                .FirstOrDefault(d => d.Type == DebugDirectoryEntryType.CodeView);

            if (codeView.DataSize > 0)
            {
                var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeView);
                pdbFileName = codeViewData.Path;
                AnsiConsole.MarkupLine($"  [dim]→ PDB reference: {pdbFileName}[/]");

                // Try to find the PDB file
                pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
            }
        }

        // If we still don't have a PDB, try downloading the symbols package
        if (pdbPath == null && pdbFileName != null)
        {
            var symbolsDir = Path.Combine(workingDirectory, "symbols");
            
            // Check if symbols directory already exists (might have been downloaded by orchestrator)
            if (!Directory.Exists(symbolsDir) || Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories).Length == 0)
            {
                AnsiConsole.MarkupLine("  [dim]→ Attempting to download symbols package...[/]");
                var snupkgDownloaded = await TryDownloadSymbolsPackageAsync(assemblyPath, workingDirectory);
                
                if (snupkgDownloaded)
                {
                    // Try to find the PDB again after downloading symbols
                    pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
                }
            }
        }

        if (pdbPath != null && File.Exists(pdbPath))
        {
            AnsiConsole.MarkupLine($"  [green]✓ Found external PDB:[/] [cyan]{Path.GetFileName(pdbPath)}[/]");
            await ExtractFromExternalPdbAsync(pdbPath);
        }
        else
        {
            var noSymbolsPanel = new Panel(
                "[yellow]No PDB found[/] - cannot extract compiler arguments\n\n" +
                "[dim]Note: Reproducible builds with embedded symbols are required for complog extraction[/]")
                .BorderColor(Color.Yellow)
                .Header("[yellow]⚠ Missing Symbols[/]");
            AnsiConsole.Write(noSymbolsPanel);
        }
    }

    private async Task ExtractFromEmbeddedPdbAsync(PEReader peReader)
    {
        var embeddedPdb = peReader.ReadDebugDirectory()
            .First(d => d.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        var pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
        var metadataReader = pdbProvider.GetMetadataReader();

        await ExtractCompilationOptionsAsync(metadataReader);
    }

    private async Task ExtractFromExternalPdbAsync(string pdbPath)
    {
        await using var pdbStream = File.OpenRead(pdbPath);
        using var metadataReaderProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var metadataReader = metadataReaderProvider.GetMetadataReader();

        await ExtractCompilationOptionsAsync(metadataReader);
    }

    private async Task ExtractCompilationOptionsAsync(MetadataReader metadataReader)
    {
        AnsiConsole.WriteLine();
        
        var compilationPanel = new Panel("")
            .Header("[cyan]Compilation Information[/]")
            .BorderColor(Color.Cyan1)
            .Expand();

        // Extract custom debug information (this is where compiler args live)
        foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
            var guid = metadataReader.GetGuid(cdi.Kind);

            // CompilationOptions GUID: {B5FEEC05-8CD0-4A83-96DA-466284BB4BD8}
            if (guid.ToString().Equals("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8", StringComparison.OrdinalIgnoreCase))
            {
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                var options = System.Text.Encoding.UTF8.GetString(blob);
                
                var argsTable = new Table()
                    .BorderColor(Color.Grey)
                    .AddColumn("[yellow]Compiler Arguments[/]");
                
                var args = ParseCompilerArguments(options);
                foreach (var arg in args)
                {
                    argsTable.AddRow($"[cyan]{arg}[/]");
                }
                AnsiConsole.Write(argsTable);
                AnsiConsole.WriteLine();
            }

            // CompilationMetadataReferences GUID: {7E4D4708-096E-4C5C-AEDA-CB10BA6A740D}
            if (guid.ToString().Equals("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D", StringComparison.OrdinalIgnoreCase))
            {
                var blobReader = metadataReader.GetBlobReader(cdi.Value);
                AnsiConsole.MarkupLine("[yellow]Metadata References:[/]");
                ParseMetadataReferences(blobReader);
                AnsiConsole.WriteLine();
            }
        }

        // Extract source files and Source Link information
        var sourceFilesTree = new Tree("[yellow]Source Files[/]");
        
        int documentCount = 0;
        var sourceLinkUrls = new Dictionary<string, string>();

        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);
            var language = metadataReader.GetGuid(document.Language);
            
            documentCount++;
            
            // Check for embedded source
            var embeddedSource = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .Where(
                    cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE",
                        StringComparison.OrdinalIgnoreCase))
                .Cast<CustomDebugInformation?>()
                .FirstOrDefault();

            if (embeddedSource.HasValue && !(embeddedSource.Value.Kind != default))
            {
                sourceFilesTree.AddNode($"[green]{name}[/] [dim](embedded)[/]");
            }
            else
            {
                sourceFilesTree.AddNode($"[cyan]{name}[/]");
            }
        }

        AnsiConsole.Write(sourceFilesTree);
        AnsiConsole.MarkupLine($"  [dim]Total: {documentCount} source files[/]");
        AnsiConsole.WriteLine();

        // Extract Source Link information
        var sourceLinkHandle = metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
            .Select(h => metadataReader.GetCustomDebugInformation(h))
            .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("CC110556-A091-4D38-9FEC-25AB9A351A6A", StringComparison.OrdinalIgnoreCase));

        if (sourceLinkHandle.Kind != default)
        {
            var blob = metadataReader.GetBlobBytes(sourceLinkHandle.Value);
            var sourceLinkJson = System.Text.Encoding.UTF8.GetString(blob);
            
            var sourceLinkPanel = new Panel(new Markup($"[dim]{sourceLinkJson}[/]"))
                .Header("[cyan]Source Link Configuration[/]")
                .BorderColor(Color.Cyan1);
            AnsiConsole.Write(sourceLinkPanel);
            AnsiConsole.WriteLine();
            
            // TODO: Parse Source Link JSON to map local paths to repository URLs
            // Format: { "documents": { "C:\\path\\*": "https://raw.githubusercontent.com/user/repo/commit/*" } }
        }

        await Task.CompletedTask;
    }

    private string[] ParseCompilerArguments(string options)
    {
        // Compiler arguments are stored as null-terminated strings
        return options.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private void ParseMetadataReferences(BlobReader blobReader)
    {
        try
        {
            var references = MetadataReferenceParser.Parse(blobReader);
            
            var refsTable = new Table()
                .BorderColor(Color.Grey)
                .AddColumn("[dim]#[/]")
                .AddColumn("[cyan]Reference[/]")
                .AddColumn("[yellow]Aliases[/]")
                .AddColumn("[dim]Properties[/]");

            int displayCount = Math.Min(references.Count, 50);
            for (int i = 0; i < displayCount; i++)
            {
                var reference = references[i];
                refsTable.AddRow(
                    $"[dim]{i + 1}[/]",
                    $"[cyan]{Path.GetFileName(reference.FileName)}[/]",
                    reference.Aliases.Count > 0 ? string.Join(", ", reference.Aliases) : "[dim]-[/]",
                    $"[dim]Embed: {reference.EmbedInteropTypes}[/]"
                );
            }

            AnsiConsole.Write(refsTable);
            if (references.Count > 50)
            {
                AnsiConsole.MarkupLine($"[dim]... and {references.Count - 50} more references[/]");
            }
            AnsiConsole.MarkupLine($"  [dim]Total: {references.Count} references[/]");

            // TODO: For complog creation, we need to:
            // 1. Identify which references are framework assemblies vs NuGet packages
            // 2. For framework assemblies: download the appropriate reference pack
            // 3. For NuGet packages: recursively process dependencies
            // 4. Preserve the exact versions and paths for reproducibility
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not fully parse metadata references: [dim]{ex.Message}[/]");
        }
    }

    private string? FindExternalPdb(string assemblyPath, string pdbFileName, string workingDirectory)
    {
        // Try multiple locations:
        // 1. Same directory as assembly
        var assemblyDir = Path.GetDirectoryName(assemblyPath);
        if (assemblyDir != null)
        {
            var pdbPath = Path.Combine(assemblyDir, Path.GetFileName(pdbFileName));
            if (File.Exists(pdbPath))
                return pdbPath;
        }

        // 2. Symbols package extraction directory
        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (Directory.Exists(symbolsDir))
        {
            var pdbPaths = Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories);
            var match = pdbPaths.FirstOrDefault(p => 
                Path.GetFileName(p).Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // 3. Original package extraction directory (some packages include PDBs)
        var extractedDir = Path.Combine(workingDirectory, "extracted");
        if (Directory.Exists(extractedDir))
        {
            var pdbPaths = Directory.GetFiles(extractedDir, "*.pdb", SearchOption.AllDirectories);
            var match = pdbPaths.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(Path.GetFileName(pdbFileName), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return null;
    }

    private async Task<bool> TryDownloadSymbolsPackageAsync(string assemblyPath, string workingDirectory)
    {
        try
        {
            // Extract package info from the assembly path
            // Path is typically: workingDirectory/extracted/lib/netX.X/PackageName.dll
            var extractedDir = Path.Combine(workingDirectory, "extracted");
            if (!assemblyPath.StartsWith(extractedDir))
            {
                return false; // Not from a NuGet package
            }

            // Try to find the .nuspec file to get package ID and version
            var nuspecFiles = Directory.GetFiles(extractedDir, "*.nuspec", SearchOption.TopDirectoryOnly);
            if (nuspecFiles.Length == 0)
            {
                return false;
            }

            var nuspecPath = nuspecFiles[0];
            var (packageId, version) = ParseNuspecFile(nuspecPath);
            
            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(version))
            {
                return false;
            }

            AnsiConsole.MarkupLine($"    [dim]Trying to download {packageId}.{version}.snupkg...[/]");

            // Try multiple symbol package sources
            var snupkgUrls = new[]
            {
                $"https://www.nuget.org/api/v2/symbolpackage/{packageId}/{version}",
                $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.snupkg",
                $"https://globalcdn.nuget.org/packages/{packageId.ToLowerInvariant()}.{version.ToLowerInvariant()}.snupkg",
            };

            using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            foreach (var snupkgUrl in snupkgUrls)
            {
                try
                {
                    var response = await httpClient.GetAsync(snupkgUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        var snupkgPath = Path.Combine(workingDirectory, $"{packageId}.{version}.snupkg");
                        using (var fileStream = File.Create(snupkgPath))
                        {
                            await response.Content.CopyToAsync(fileStream);
                        }

                        // Extract the symbols package
                        var symbolsDir = Path.Combine(workingDirectory, "symbols");
                        Directory.CreateDirectory(symbolsDir);
                        ZipFile.ExtractToDirectory(snupkgPath, symbolsDir, overwriteFiles: true);

                        var pdbCount = Directory.GetFiles(symbolsDir, "*.pdb", SearchOption.AllDirectories).Length;
                        AnsiConsole.MarkupLine($"    [green]✓ Downloaded and extracted symbols package ({pdbCount} PDB files)[/]");
                        return true;
                    }
                }
                catch
                {
                    // Try next URL
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private (string packageId, string version) ParseNuspecFile(string nuspecPath)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(nuspecPath);
            var ns = doc.Root?.Name.Namespace;
            if (ns == null) return ("", "");
            
            var metadata = doc.Root?.Element(ns + "metadata");
            
            var packageId = metadata?.Element(ns + "id")?.Value ?? "";
            var version = metadata?.Element(ns + "version")?.Value ?? "";
            
            return (packageId, version);
        }
        catch
        {
            return ("", "");
        }
    }
}
