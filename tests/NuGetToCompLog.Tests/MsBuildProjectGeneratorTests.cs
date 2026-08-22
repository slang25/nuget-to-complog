using System.Xml.Linq;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.Patch;
using NuGetToCompLog.Services.Reconstruction;
using Xunit;

namespace NuGetToCompLog.Tests;

public class MsBuildProjectGeneratorTests : IDisposable
{
    private readonly string _tempDir;

    public MsBuildProjectGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"msbuild-gen-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private PackageExtractionResult CreateExtraction(
        string[]? compilerArgLines = null,
        string? nuspec = null,
        string tfm = "netstandard2.0")
    {
        var workingDir = Path.Combine(_tempDir, "working");
        var extractPath = Path.Combine(workingDir, "extracted");
        var sourcesDir = Path.Combine(workingDir, "sources");
        Directory.CreateDirectory(extractPath);
        Directory.CreateDirectory(sourcesDir);

        string? argsFile = null;
        if (compilerArgLines != null)
        {
            argsFile = Path.Combine(workingDir, "compiler-arguments.txt");
            File.WriteAllLines(argsFile, compilerArgLines);
        }

        if (nuspec != null)
        {
            File.WriteAllText(Path.Combine(extractPath, "TestPackage.nuspec"), nuspec);
        }

        return new PackageExtractionResult(
            Package: new PackageIdentity("TestPackage", "1.2.3"),
            WorkingDirectory: workingDir,
            ExtractPath: extractPath,
            SelectedTfm: tfm,
            SelectedAssemblies: [],
            CompilerArgsFile: argsFile,
            MetadataRefsFile: null,
            SourcesDirectory: sourcesDir,
            ResourcesDirectory: null,
            Ledger: new ReconstructionLedger());
    }

    private string CreatePatchDir(params (string RelativePath, string Content)[] sources)
    {
        var patchDir = Path.Combine(_tempDir, "patches", "TestPackage+1.2.3");
        Directory.CreateDirectory(Path.Combine(patchDir, "src"));
        foreach (var (relativePath, content) in sources)
        {
            var path = Path.Combine(patchDir, "src", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return patchDir;
    }

    private static MsBuildProjectGenerator CreateGenerator() => new(new SilentConsole());

    [Fact]
    public async Task Generates_Csproj_With_Package_Identity()
    {
        var extraction = CreateExtraction(compilerArgLines:
        [
            "language-version",
            "9.0",
            "define",
            "TRACE,RELEASE,NETSTANDARD2_0,MY_CUSTOM",
            "optimization",
            "release",
        ]);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        Assert.Equal(Path.Combine(patchDir, "TestPackage.csproj"), csprojPath);
        var doc = XDocument.Load(csprojPath);

        string Property(string name) => doc.Descendants(name).Single().Value;
        Assert.Equal("netstandard2.0", Property("TargetFramework"));
        Assert.Equal("TestPackage", Property("AssemblyName"));
        Assert.Equal("TestPackage", Property("PackageId"));
        Assert.Equal("1.2.3", Property("Version"));
        Assert.Equal("9.0", Property("LangVersion"));
        Assert.Equal("true", Property("Optimize"));
        Assert.Equal("false", Property("EnableDefaultItems"));
        Assert.Equal("$(DefineConstants);MY_CUSTOM", Property("DefineConstants"));
        Assert.Equal("src/**/*.cs", (string?)doc.Descendants("Compile").Single().Attribute("Include"));
    }

    [Fact]
    public async Task Filters_Implicit_Defines_Entirely()
    {
        var extraction = CreateExtraction(compilerArgLines:
        [
            "define",
            "TRACE;RELEASE;NETSTANDARD2_0;NET5_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER",
        ]);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        var doc = XDocument.Load(csprojPath);
        Assert.Empty(doc.Descendants("DefineConstants"));
    }

    [Fact]
    public async Task Declares_Nuspec_Dependencies_For_Nearest_Tfm()
    {
        var extraction = CreateExtraction(
            compilerArgLines: [],
            nuspec: """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>TestPackage</id>
                    <version>1.2.3</version>
                    <authors>test</authors>
                    <description>test</description>
                    <dependencies>
                      <group targetFramework=".NETStandard2.0">
                        <dependency id="System.Memory" version="4.5.5" />
                        <dependency id="Newtonsoft.Json" version="[13.0.3, )" />
                      </group>
                      <group targetFramework="net6.0" />
                    </dependencies>
                  </metadata>
                </package>
                """);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        var doc = XDocument.Load(csprojPath);
        var references = doc.Descendants("PackageReference")
            .ToDictionary(e => (string?)e.Attribute("Include"), e => (string?)e.Attribute("Version"));
        Assert.Equal("4.5.5", references["System.Memory"]);
        Assert.Equal("13.0.3", references["Newtonsoft.Json"]);
    }

    [Fact]
    public async Task Disables_Generated_Attributes_When_Sources_Contain_Them()
    {
        var extraction = CreateExtraction(compilerArgLines: []);
        var patchDir = CreatePatchDir(
            ("Class1.cs", "public class Class1 { }"),
            (Path.Combine("obj", "TestPackage.AssemblyInfo.cs"),
                """
                [assembly: System.Reflection.AssemblyVersionAttribute("1.2.3.0")]
                """),
            (Path.Combine("obj", ".NETStandard,Version=v2.0.AssemblyAttributes.cs"),
                """
                [assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETStandard,Version=v2.0")]
                """));

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        var doc = XDocument.Load(csprojPath);
        Assert.Equal("false", doc.Descendants("GenerateAssemblyInfo").Single().Value);
        Assert.Equal("false", doc.Descendants("GenerateTargetFrameworkAttribute").Single().Value);
    }

    [Fact]
    public async Task Keeps_Generated_Attributes_When_Sources_Lack_Them()
    {
        var extraction = CreateExtraction(compilerArgLines: []);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        var doc = XDocument.Load(csprojPath);
        Assert.Empty(doc.Descendants("GenerateAssemblyInfo"));
        Assert.Empty(doc.Descendants("GenerateTargetFrameworkAttribute"));
    }

    [Fact]
    public async Task Writes_Isolation_Stubs()
    {
        var extraction = CreateExtraction(compilerArgLines: []);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));

        await CreateGenerator().GenerateAsync(extraction, patchDir);

        Assert.True(File.Exists(Path.Combine(patchDir, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(patchDir, "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(patchDir, "Directory.Packages.props")));
    }

    [Fact]
    public async Task Maps_Embedded_Resources_With_Logical_Names()
    {
        var extraction = CreateExtraction(compilerArgLines: []);
        var patchDir = CreatePatchDir(("Class1.cs", "public class Class1 { }"));
        Directory.CreateDirectory(Path.Combine(patchDir, "resources"));
        File.WriteAllText(Path.Combine(patchDir, "resources", "TestPackage.Strings.resources"), "x");
        File.WriteAllText(Path.Combine(patchDir, "resource-mappings.txt"),
            "TestPackage.Strings.resources|TestPackage.Strings.resources");

        var csprojPath = await CreateGenerator().GenerateAsync(extraction, patchDir);

        var doc = XDocument.Load(csprojPath);
        var resource = doc.Descendants("EmbeddedResource").Single();
        Assert.Equal("resources/TestPackage.Strings.resources", (string?)resource.Attribute("Include"));
        Assert.Equal("TestPackage.Strings.resources", resource.Element("LogicalName")?.Value);
    }

    private sealed class SilentConsole : IConsoleWriter
    {
        public void MarkupLine(string markup) { }
        public void WriteLine() { }
        public void WriteException(Exception exception) { }
        public void WritePanel(string header, string content, string? borderColor = null) { }
        public void WriteTree(string rootLabel, Dictionary<string, List<string>> nodes) { }
        public void WriteTable(string[] headers, List<string[]> rows) { }
        public Task ExecuteWithStatusAsync(string status, Func<Task> action) => action();
        public void SetIndeterminateProgress() { }
        public void ClearProgress() { }
    }
}
