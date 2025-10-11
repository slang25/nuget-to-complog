using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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
                Console.WriteLine("  ✓ Found embedded PDB");
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
                Console.WriteLine($"  → PDB reference: {pdbFileName}");

                // Try to find the PDB file
                pdbPath = FindExternalPdb(assemblyPath, pdbFileName, workingDirectory);
            }
        }

        if (pdbPath != null && File.Exists(pdbPath))
        {
            Console.WriteLine($"  ✓ Found external PDB: {Path.GetFileName(pdbPath)}");
            await ExtractFromExternalPdbAsync(pdbPath);
        }
        else
        {
            Console.WriteLine("  ✗ No PDB found - cannot extract compiler arguments");
            Console.WriteLine("    Note: Reproducible builds with embedded symbols are required for complog extraction");
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
        Console.WriteLine();
        Console.WriteLine("  COMPILATION OPTIONS:");
        Console.WriteLine("  " + new string('-', 76));

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
                
                Console.WriteLine("  Compiler Arguments:");
                var args = ParseCompilerArguments(options);
                foreach (var arg in args)
                {
                    Console.WriteLine($"    {arg}");
                }
                Console.WriteLine();
            }

            // CompilationMetadataReferences GUID: {7E4D4708-096E-4C5C-AEDA-CB10BA6A740D}
            if (guid.ToString().Equals("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D", StringComparison.OrdinalIgnoreCase))
            {
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                Console.WriteLine("  Metadata References:");
                ParseMetadataReferences(blob);
                Console.WriteLine();
            }
        }

        // Extract source files and Source Link information
        Console.WriteLine("  SOURCE FILES:");
        Console.WriteLine("  " + new string('-', 76));
        
        int documentCount = 0;
        var sourceLinkUrls = new Dictionary<string, string>();

        foreach (var docHandle in metadataReader.Documents)
        {
            var document = metadataReader.GetDocument(docHandle);
            var name = metadataReader.GetString(document.Name);
            var language = metadataReader.GetGuid(document.Language);
            
            documentCount++;
            Console.WriteLine($"    [{documentCount}] {name}");

            // Check for embedded source
            var embeddedSource = metadataReader.GetCustomDebugInformation(docHandle)
                .Select(h => metadataReader.GetCustomDebugInformation(h))
                .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("0E8A571B-6926-466E-B4AD-8AB04611F5FE", StringComparison.OrdinalIgnoreCase));

            if (embeddedSource.Kind != default)
            {
                Console.WriteLine($"        → Has embedded source");
            }
        }

        Console.WriteLine($"    Total: {documentCount} source files");
        Console.WriteLine();

        // Extract Source Link information
        var sourceLinkHandle = metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
            .Select(h => metadataReader.GetCustomDebugInformation(h))
            .FirstOrDefault(cdi => metadataReader.GetGuid(cdi.Kind).ToString().Equals("CC110556-A091-4D38-9FEC-25AB9A351A6A", StringComparison.OrdinalIgnoreCase));

        if (sourceLinkHandle.Kind != default)
        {
            var blob = metadataReader.GetBlobBytes(sourceLinkHandle.Value);
            var sourceLinkJson = System.Text.Encoding.UTF8.GetString(blob);
            
            Console.WriteLine("  SOURCE LINK CONFIGURATION:");
            Console.WriteLine("  " + new string('-', 76));
            Console.WriteLine(sourceLinkJson);
            Console.WriteLine();
            
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
            Console.WriteLine($"    Total references: {count}");
            Console.WriteLine();

            for (int i = 0; i < count; i++)
            {
                int fileNameLength = reader.ReadInt32();
                var fileName = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(fileNameLength));
                
                Console.WriteLine($"    [{i + 1}] {fileName}");

                // Read extern aliases count
                int aliasCount = reader.ReadInt32();
                if (aliasCount > 0)
                {
                    Console.WriteLine($"        Extern aliases: {aliasCount}");
                    for (int j = 0; j < aliasCount; j++)
                    {
                        int aliasLength = reader.ReadInt32();
                        var alias = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(aliasLength));
                        Console.WriteLine($"          - {alias}");
                    }
                }

                // Read properties (simplified - actual format has more)
                // byte embedInteropTypes = reader.ReadByte();
                // This part can be extended based on actual requirements
            }

            // TODO: For complog creation, we need to:
            // 1. Identify which references are framework assemblies vs NuGet packages
            // 2. For framework assemblies: download the appropriate reference pack
            // 3. For NuGet packages: recursively process dependencies
            // 4. Preserve the exact versions and paths for reproducibility
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Warning: Could not fully parse metadata references: {ex.Message}");
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
