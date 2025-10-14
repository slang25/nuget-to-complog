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

                if (hasEmbeddedPdb)
                {
                    args.Add("/debug:embedded");
                }

                if (hasReproducibleMarker)
                {
                    args.Add("/deterministic+");
                }

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
        return options.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
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
