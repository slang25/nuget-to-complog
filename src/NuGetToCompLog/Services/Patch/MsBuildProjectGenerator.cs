using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGetToCompLog.Abstractions;
using PackageExtractionResult = NuGetToCompLog.Domain.PackageExtractionResult;

namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Generates an SDK-style .csproj over an ejected package's src/ tree so MSBuild can build it
/// and a consuming project can take a ProjectReference to it. The csproj approximates the
/// original compilation (the byte-exact path remains build.rsp + apply); its job is to be a
/// faithful-enough, editable stand-in that restores and builds with plain `dotnet build`.
///
/// The generated project declares the package's own identity (PackageId, AssemblyName, Version)
/// and re-declares the package's nuspec dependencies, so NuGet's "a project wins over a package
/// of the same identity" rule substitutes it everywhere in the consumer's graph, including for
/// other packages that depend on the swapped package transitively.
/// </summary>
public class MsBuildProjectGenerator
{
    private readonly IConsoleWriter _console;

    public MsBuildProjectGenerator(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Writes {AssemblyName}.csproj plus Directory.Build.props/.targets and
    /// Directory.Packages.props stubs into the patch directory. The stubs stop MSBuild's upward
    /// file search so the consuming repo's central package management, analyzers, or custom
    /// targets cannot leak into the reconstructed compilation. Returns the csproj path.
    /// </summary>
    public async Task<string> GenerateAsync(PackageExtractionResult extraction, string patchDir)
    {
        var assemblyName = extraction.SelectedAssemblies.Count > 0
            ? Path.GetFileNameWithoutExtension(extraction.SelectedAssemblies[0])
            : extraction.Package.Id;

        var (argsDict, _) = extraction.CompilerArgsFile != null
            ? CompilerArgumentsFile.Parse(await File.ReadAllLinesAsync(extraction.CompilerArgsFile))
            : (new Dictionary<string, string>(), new List<string>());

        var tfm = extraction.SelectedTfm ?? "netstandard2.0";

        var propertyGroup = new XElement("PropertyGroup",
            new XElement("TargetFramework", tfm),
            new XElement("AssemblyName", assemblyName),
            new XElement("PackageId", extraction.Package.Id),
            new XElement("Version", extraction.Package.Version),
            new XElement("EnableDefaultItems", "false"),
            new XElement("AllowUnsafeBlocks", "true"),
            new XElement("DebugType", "portable"),
            new XElement("NoWarn", "$(NoWarn);CS8632"));

        if (argsDict.TryGetValue("language-version", out var langVersion))
        {
            propertyGroup.Add(new XElement("LangVersion", langVersion));
        }

        if (argsDict.TryGetValue("nullable", out var nullable) &&
            !nullable.Equals("Disable", StringComparison.OrdinalIgnoreCase))
        {
            propertyGroup.Add(new XElement("Nullable", nullable.ToLowerInvariant()));
        }

        if (argsDict.TryGetValue("define", out var defines))
        {
            var custom = defines
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Where(d => !IsImplicitDefine(d))
                .ToList();
            if (custom.Count > 0)
            {
                propertyGroup.Add(new XElement("DefineConstants",
                    "$(DefineConstants);" + string.Join(";", custom)));
            }
        }

        if (argsDict.TryGetValue("optimization", out var optimization))
        {
            var optimize = optimization.StartsWith("release", StringComparison.OrdinalIgnoreCase);
            propertyGroup.Add(new XElement("Optimize", optimize ? "true" : "false"));
        }

        if (argsDict.TryGetValue("checksum-algorithm", out var checksumAlgorithm))
        {
            propertyGroup.Add(new XElement("ChecksumAlgorithm", checksumAlgorithm));
        }

        // Sources recovered from the PDB include any committed or generated assembly-level
        // attributes, so the SDK's generated ones would collide with CS0579.
        var (hasAssemblyAttributes, hasTargetFrameworkAttribute) = ScanForAssemblyAttributes(
            Path.Combine(patchDir, "src"));
        if (hasAssemblyAttributes)
        {
            propertyGroup.Add(new XElement("GenerateAssemblyInfo", "false"));
        }
        if (hasTargetFrameworkAttribute)
        {
            propertyGroup.Add(new XElement("GenerateTargetFrameworkAttribute", "false"));
        }

        // Public-sign with the original key so the assembly keeps its strong-name identity
        // (InternalsVisibleTo lists and friend assemblies keep working).
        if (extraction.SelectedAssemblies.Count > 0 &&
            StrongNameUtil.TryGetStrongNameInfo(extraction.SelectedAssemblies[0]) is { } strongName)
        {
            var keyPath = Path.Combine(patchDir, "public.snk");
            await File.WriteAllBytesAsync(keyPath, strongName.PublicKey);
            propertyGroup.Add(new XElement("SignAssembly", "true"));
            propertyGroup.Add(new XElement("PublicSign", "true"));
            propertyGroup.Add(new XElement("AssemblyOriginatorKeyFile", "public.snk"));
        }

        var project = new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XComment(
                $" Generated by nuget-to-complog swap from {extraction.Package.Id} {extraction.Package.Version}. " +
                "Approximates the original compilation; build.rsp remains the byte-exact rebuild path. "),
            propertyGroup,
            new XElement("ItemGroup",
                new XElement("Compile", new XAttribute("Include", "src/**/*.cs"))));

        var resources = ReadResourceMappings(patchDir);
        if (resources.Count > 0)
        {
            project.Add(new XElement("ItemGroup",
                resources.Select(r => new XElement("EmbeddedResource",
                    new XAttribute("Include", $"resources/{r.FileName}"),
                    new XElement("LogicalName", r.LogicalName)))));
        }

        var (dependencies, frameworkReferences) = ReadNuspecReferences(extraction, tfm);
        if (dependencies.Count > 0)
        {
            project.Add(new XElement("ItemGroup",
                dependencies.Select(d => new XElement("PackageReference",
                    new XAttribute("Include", d.Id),
                    new XAttribute("Version", d.Version)))));
        }

        // A package that declares a <frameworkReference> (Microsoft.AspNetCore.App and friends)
        // compiled against that shared framework, so its recovered source needs the same one.
        if (frameworkReferences.Count > 0)
        {
            project.Add(new XElement("ItemGroup",
                frameworkReferences.Select(name => new XElement("FrameworkReference",
                    new XAttribute("Include", name)))));
        }

        var csprojPath = Path.Combine(patchDir, $"{assemblyName}.csproj");
        await File.WriteAllTextAsync(csprojPath, project.ToString() + Environment.NewLine);

        await WriteIsolationStubsAsync(patchDir);

        var frameworkSummary = frameworkReferences.Count > 0
            ? $", {frameworkReferences.Count} framework references"
            : string.Empty;
        _console.MarkupLine($"  [green]✓[/] Generated {Path.GetFileName(csprojPath)} " +
            $"({dependencies.Count} package dependencies{frameworkSummary})");

        return csprojPath;
    }

    /// <summary>
    /// DEBUG/RELEASE/TRACE and TFM constants are re-derived by the SDK from the configuration
    /// and TargetFramework, so carrying them over verbatim would pin a Release-build's constants
    /// onto every configuration.
    /// </summary>
    private static bool IsImplicitDefine(string define)
    {
        if (define is "DEBUG" or "RELEASE" or "TRACE" or "NET")
        {
            return true;
        }

        return define.StartsWith("NETSTANDARD", StringComparison.Ordinal) ||
               define.StartsWith("NETCOREAPP", StringComparison.Ordinal) ||
               define.StartsWith("NETFRAMEWORK", StringComparison.Ordinal) ||
               (define.StartsWith("NET", StringComparison.Ordinal) &&
                define.Length > 3 && char.IsDigit(define[3]));
    }

    private static (bool HasAssemblyAttributes, bool HasTargetFrameworkAttribute) ScanForAssemblyAttributes(string srcDir)
    {
        var hasAssemblyAttributes = false;
        var hasTargetFrameworkAttribute = false;

        if (!Directory.Exists(srcDir))
        {
            return (false, false);
        }

        foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            if (!hasAssemblyAttributes &&
                (content.Contains("[assembly: System.Reflection.Assembly") ||
                 content.Contains("[assembly: Assembly")))
            {
                hasAssemblyAttributes = true;
            }
            if (!hasTargetFrameworkAttribute && content.Contains("TargetFrameworkAttribute"))
            {
                hasTargetFrameworkAttribute = true;
            }
            if (hasAssemblyAttributes && hasTargetFrameworkAttribute)
            {
                break;
            }
        }

        return (hasAssemblyAttributes, hasTargetFrameworkAttribute);
    }

    private static List<(string FileName, string LogicalName)> ReadResourceMappings(string patchDir)
    {
        var mappingsFile = Path.Combine(patchDir, "resource-mappings.txt");
        if (!File.Exists(mappingsFile))
        {
            return [];
        }

        return File.ReadAllLines(mappingsFile)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    /// <summary>
    /// Reads the package's own dependencies for the target framework: both the package
    /// dependencies and the shared-framework references, each taken from the nuspec group
    /// nearest to <paramref name="tfm"/> the way NuGet would pick it at restore time.
    /// </summary>
    private (List<(string Id, string Version)> Dependencies, List<string> FrameworkReferences) ReadNuspecReferences(
        PackageExtractionResult extraction,
        string tfm)
    {
        try
        {
            var nuspecPath = Directory.Exists(extraction.ExtractPath)
                ? Directory.GetFiles(extraction.ExtractPath, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (nuspecPath == null)
            {
                return ([], []);
            }

            var reader = new NuspecReader(nuspecPath);
            var target = NuGetFramework.Parse(tfm);

            var dependencies = NearestGroup(reader.GetDependencyGroups().ToList(), g => g.TargetFramework, target)
                ?.Packages
                .Select(p => (p.Id, p.VersionRange.MinVersion?.ToString() ?? p.VersionRange.ToShortString()))
                .ToList() ?? [];

            var frameworkReferences = NearestGroup(
                    reader.GetFrameworkRefGroups().ToList(), g => g.TargetFramework, target)
                ?.FrameworkReferences
                .Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            return (dependencies, frameworkReferences);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"  [yellow]⚠[/] Could not read nuspec dependencies: {ex.Message}");
            return ([], []);
        }
    }

    private static TGroup? NearestGroup<TGroup>(
        List<TGroup> groups,
        Func<TGroup, NuGetFramework> frameworkOf,
        NuGetFramework target)
        where TGroup : class
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var nearest = new FrameworkReducer().GetNearest(target, groups.Select(frameworkOf));
        return groups.FirstOrDefault(g => Equals(frameworkOf(g), nearest));
    }

    private static async Task WriteIsolationStubsAsync(string patchDir)
    {
        var stub = "<Project />" + Environment.NewLine;
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            var path = Path.Combine(patchDir, name);
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, stub);
            }
        }
    }
}
