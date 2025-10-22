using System.Reflection.Metadata;
using NuGetToCompLog.Domain;

namespace NuGetToCompLog.Services.Pdb;

/// <summary>
/// Extracts compiler options and metadata references from PDB custom debug information.
/// </summary>
public class CompilationOptionsExtractor
{
    // Custom debug information GUIDs
    private const string CompilationOptionsGuid = "B5FEEC05-8CD0-4A83-96DA-466284BB4BD8";
    private const string MetadataReferencesGuid = "7E4D4708-096E-4C5C-AEDA-CB10BA6A740D";

    /// <summary>
    /// Extracts compilation information from a PDB metadata reader.
    /// </summary>
    public async Task<CompilationInfo> ExtractCompilationInfoAsync(
        MetadataReader metadataReader,
        bool hasEmbeddedPdb,
        bool hasReproducibleMarker,
        CancellationToken cancellationToken = default)
    {
        var compilerArgs = new List<string>();
        var metadataRefs = new List<MetadataReference>();
        string? targetFramework = null;

        foreach (var cdiHandle in metadataReader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = metadataReader.GetCustomDebugInformation(cdiHandle);
            var guid = metadataReader.GetGuid(cdi.Kind);

            if (guid.ToString().Equals(CompilationOptionsGuid, StringComparison.OrdinalIgnoreCase))
            {
                var blob = metadataReader.GetBlobBytes(cdi.Value);
                var options = System.Text.Encoding.UTF8.GetString(blob);

                var args = ParseCompilerArguments(options);

                // Note: Debug flags (/debug:portable, /embed-, /deterministic+) are NOT added here
                // They will be generated later from DebugConfiguration in CompLogFileCreator
                // to avoid duplication and ensure correct ordering

                compilerArgs.AddRange(args);

                targetFramework = ExtractTargetFramework(args);
            }

            if (guid.ToString().Equals(MetadataReferencesGuid, StringComparison.OrdinalIgnoreCase))
            {
                var blobReader = metadataReader.GetBlobReader(cdi.Value);
                metadataRefs = MetadataReferenceParser.Parse(blobReader);
            }
        }

        return await Task.FromResult(new CompilationInfo(
            compilerArgs,
            metadataRefs,
            targetFramework,
            hasEmbeddedPdb,
            hasReproducibleMarker));
    }

    private List<string> ParseCompilerArguments(string options)
    {
        // Compiler arguments are stored as null-terminated strings
        var args = options.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        
        // Filter out debug-related flags that will be generated later from DebugConfiguration
        // These include /debug:*, /embed*, /deterministic+, etc.
        args = args.Where(arg => 
            !arg.StartsWith("/debug:", StringComparison.OrdinalIgnoreCase) &&
            !arg.StartsWith("/embed", StringComparison.OrdinalIgnoreCase) &&
            !arg.Equals("/deterministic+", StringComparison.OrdinalIgnoreCase)).ToList();
        
        // The PDB only contains a subset of compiler arguments (key-value pairs + some flags).
        // Add common compiler flags that are typically present but not stored in PDB.
        // These are MSBuild-generated arguments that ensure compatibility with original builds.
        // Note: We do NOT include /nowarn or /warnaserror as we cannot determine these from the package.
        var additionalArgs = new List<string>
        {
            "/unsafe-",
            "/checked-",
            "/fullpaths",
            "/nostdlib+",
            "/errorreport:prompt"
        };
        
        // Insert these at the beginning to match typical csc.exe argument order
        args.InsertRange(0, additionalArgs);
        
        return args;
    }

    private string? ExtractTargetFramework(List<string> args)
    {
        var defineArg = args.FirstOrDefault(a => a.StartsWith("/define:", StringComparison.OrdinalIgnoreCase));
        if (defineArg == null)
        {
            return null;
        }

        var defines = defineArg.Substring(8).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var define in defines)
        {
            if (define.StartsWith("NET", StringComparison.Ordinal) && define.Contains("_"))
            {
                return define.Replace("_", ".").ToLowerInvariant();
            }
        }

        return null;
    }
}
