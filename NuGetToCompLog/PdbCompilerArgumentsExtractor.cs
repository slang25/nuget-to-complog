using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
        // 3. External PDB in symbols package

        string? pdbPath = null;

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
                var pdbFileName = codeViewData.Path;
                AnsiConsole.MarkupLine($"  [dim]→ PDB reference: {pdbFileName}[/]");

                // Try to find the PDB file
                pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
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
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                AnsiConsole.MarkupLine("[yellow]Metadata References:[/]");
                ParseMetadataReferences(blob);
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
                .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE", StringComparison.OrdinalIgnoreCase));

            if (embeddedSource.Kind != default)
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

    private void ParseMetadataReferences(byte[] blob)
    {
        // Metadata references are stored in a custom binary format
        // This is a simplified parser - the actual format is more complex
        // Format: count (4 bytes), then for each reference:
        //   - file name length (4 bytes)
        //   - file name (UTF-8)
        //   - extern alias count (4 bytes)
        //   - extern aliases...
        //   - properties (embed interop types, etc.)
        
        using var stream = new MemoryStream(blob);
        using var reader = new BinaryReader(stream);

        try
        {
            int count = reader.ReadInt32();
            
            var refsTable = new Table()
                .BorderColor(Color.Grey)
                .AddColumn("[dim]#[/]")
                .AddColumn("[cyan]Reference[/]")
                .AddColumn("[yellow]Aliases[/]");

            for (int i = 0; i < count && i < 50; i++) // Limit to 50 for display
            {
                int fileNameLength = reader.ReadInt32();
                var fileName = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(fileNameLength));
                
                // Read extern aliases count
                int aliasCount = reader.ReadInt32();
                var aliases = new List<string>();
                for (int j = 0; j < aliasCount; j++)
                {
                    int aliasLength = reader.ReadInt32();
                    var alias = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(aliasLength));
                    aliases.Add(alias);
                }

                refsTable.AddRow(
                    $"[dim]{i + 1}[/]",
                    $"[cyan]{Path.GetFileName(fileName)}[/]",
                    aliases.Count > 0 ? string.Join(", ", aliases) : "[dim]-[/]"
                );
            }

            AnsiConsole.Write(refsTable);
            if (count > 50)
            {
                AnsiConsole.MarkupLine($"[dim]... and {count - 50} more references[/]");
            }
            AnsiConsole.MarkupLine($"  [dim]Total: {count} references[/]");

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
}
