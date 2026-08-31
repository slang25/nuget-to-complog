using NuGetToCompLog.Services.SourceBuild;
using NuGetToCompLog.SourceBuild;
using NuGetToCompLog.Services.Swap;
using Xunit;

namespace NuGetToCompLog.Tests;

public class SourceBuildMarkerTests : IDisposable
{
    private readonly string _tempDir;

    public SourceBuildMarkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sourcebuild-marker-{Guid.NewGuid():N}");
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

    private string WriteProject(string content)
    {
        var path = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MarkSourceBuild_AddsAttributeAndLeavesEverythingElseAlone()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <!-- keep me -->
                <PackageReference Include="Serilog" Version="4.4.0" />
                <PackageReference Include="Other" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(1, PackageReferenceSwapper.MarkSourceBuild(path, "Serilog"));

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <!-- keep me -->
                <PackageReference Include="Serilog" Version="4.4.0" SourceBuild="true" />
                <PackageReference Include="Other" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """, File.ReadAllText(path));
    }

    [Fact]
    public void MarkSourceBuild_MarksEveryConditionalItemForThePackage()
    {
        // A multi-targeting project references the same package from several ItemGroups; leaving
        // one unmarked would build that framework against the published binary.
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.0'">
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Equal(2, PackageReferenceSwapper.MarkSourceBuild(path, "Serilog"));
        Assert.Equal(2, File.ReadAllText(path).Split("SourceBuild=\"true\"").Length - 1);
    }

    [Fact]
    public void MarkSourceBuild_IsIdempotent()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.MarkSourceBuild(path, "Serilog");
        var afterFirst = File.ReadAllText(path);

        Assert.Equal(0, PackageReferenceSwapper.MarkSourceBuild(path, "Serilog"));
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void MarkSourceBuild_KeepsSpacingOfATagWithNoSpaceBeforeTheSlash()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0"/>
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.MarkSourceBuild(path, "Serilog");

        Assert.Contains("""<PackageReference Include="Serilog" Version="4.4.0" SourceBuild="true"/>""", File.ReadAllText(path));
    }

    [Fact]
    public void MarkSourceBuild_HandlesAVersionChildElement()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog">
                  <Version>4.4.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.MarkSourceBuild(path, "Serilog");

        var text = File.ReadAllText(path);
        Assert.Contains("""<PackageReference Include="Serilog" SourceBuild="true">""", text);
        Assert.Contains("<Version>4.4.0</Version>", text);
    }

    [Fact]
    public void UnmarkSourceBuild_RestoresTheOriginalText()
    {
        const string original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """;
        var path = WriteProject(original);

        PackageReferenceSwapper.MarkSourceBuild(path, "Serilog");
        Assert.Equal(1, PackageReferenceSwapper.UnmarkSourceBuild(path, "Serilog"));

        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureBuildPackageReference_SitsWithTheOtherPackageReferences()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(PackageReferenceSwapper.EnsureBuildPackageReference(path, "NuGetToCompLog.SourceBuild", "0.4.0"));

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
                <PackageReference Include="NuGetToCompLog.SourceBuild" Version="0.4.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureBuildPackageReference_IsIdempotentAndLeavesAnExistingVersionAlone()
    {
        // Whoever pinned it - a person, Renovate, a Directory.Build.props - keeps deciding.
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="NuGetToCompLog.SourceBuild" Version="0.1.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        var before = File.ReadAllText(path);

        Assert.False(PackageReferenceSwapper.EnsureBuildPackageReference(path, "NuGetToCompLog.SourceBuild", "0.4.0"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void EnsureBuildPackageReference_CreatesAnItemGroupWhenThereIsNone()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(PackageReferenceSwapper.EnsureBuildPackageReference(path, "NuGetToCompLog.SourceBuild", "0.4.0"));

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="NuGetToCompLog.SourceBuild" Version="0.4.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """, File.ReadAllText(path));
    }
}

public class SourceBuildCacheTests : IDisposable
{
    private readonly string _root;

    public SourceBuildCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sourcebuild-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<(SourceBuildCache Cache, SourceBuildProvenance Provenance)> StoreAsync(string content = "assembly")
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var assembly = Path.Combine(staging, "Serilog.dll");
        var complog = Path.Combine(staging, "Serilog.4.4.0.complog");
        await File.WriteAllTextAsync(assembly, content);
        await File.WriteAllTextAsync(complog, "complog");

        var cache = new SourceBuildCache(Path.Combine(_root, "cache"));
        var provenance = await cache.StoreAsync(
            "Serilog", "4.4.0", "net8.0", assembly, null, complog, null,
            BinaryEquivalence.ContentEquivalent, ["MVID"], "4.8.0", true, true, "exact");
        return (cache, provenance);
    }

    [Fact]
    public async Task Store_KeepsTheComplogBesideTheAssemblySoTheSourceStaysRecoverable()
    {
        var (cache, _) = await StoreAsync();

        var directory = cache.DirectoryFor("Serilog", "4.4.0", "net8.0");
        Assert.True(File.Exists(Path.Combine(directory, "Serilog.dll")));
        Assert.True(File.Exists(Path.Combine(directory, "Serilog.4.4.0.complog")));
        Assert.True(File.Exists(Path.Combine(directory, "provenance.json")));
    }

    [Fact]
    public async Task Provenance_RecordsWhatWasAcceptedAndWhy()
    {
        var (cache, provenance) = await StoreAsync();

        var read = cache.TryReadProvenance("Serilog", "4.4.0", "net8.0");
        Assert.NotNull(read);
        Assert.Equal(BinaryEquivalence.ContentEquivalent, read.Equivalence);
        Assert.Equal(["MVID"], read.AcceptedDifferences);
        Assert.Equal("4.8.0", read.Compiler);
        Assert.Equal("exact", read.ReconstructionOutlook);
        Assert.Equal(provenance.Sha256, read.Sha256);
    }

    [Fact]
    public async Task Contains_TreatsAHashMismatchAsAMissSoAStaleFileIsNeverUsed()
    {
        var (cache, provenance) = await StoreAsync();

        Assert.True(cache.Contains("Serilog", "4.4.0", "net8.0", "Serilog.dll"));
        Assert.False(cache.Contains("Serilog", "9.9.9", "net8.0", "Serilog.dll"));

        // A cached file that no longer hashes to what its own provenance recorded has been
        // truncated or edited, and must never be substituted into a build.
        File.WriteAllText(cache.AssemblyPath("Serilog", "4.4.0", "net8.0", "Serilog.dll"), "tampered");
        Assert.False(cache.Contains("Serilog", "4.4.0", "net8.0", "Serilog.dll"));
        Assert.NotEqual(string.Empty, provenance.Sha256);
    }

    [Fact]
    public async Task Store_ReplacesAPreviousBuildRatherThanMergingWithIt()
    {
        var (cache, _) = await StoreAsync();
        File.WriteAllText(Path.Combine(cache.DirectoryFor("Serilog", "4.4.0", "net8.0"), "stale.dll"), "x");

        await StoreAsync("different");

        Assert.False(File.Exists(Path.Combine(cache.DirectoryFor("Serilog", "4.4.0", "net8.0"), "stale.dll")));
    }
}

public class AssemblySurfaceComparerTests : IDisposable
{
    private readonly string _tempDir;

    public AssemblySurfaceComparerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"surface-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Compiles a snippet to a real assembly. Optimization is varied between the two sides of
    /// each comparison so the bytes genuinely differ, which is the situation the comparer exists
    /// for - a rebuild on a toolchain that is not the original one.
    /// </summary>
    private string Compile(string source, string name, Microsoft.CodeAnalysis.OptimizationLevel optimization)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p))
            .ToList();

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            name,
            [Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseSyntaxTree(source)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: optimization));

        var path = Path.Combine(_tempDir, $"{name}.dll");
        using var stream = File.Create(path);
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    private const string Library = """
        namespace Demo;
        public class Greeter
        {
            public string Greet(string name) => "hi " + name;
            public int Count { get; set; }
            private int _hidden;
            internal void Internal() { _hidden++; }
        }
        public interface IThing { void Do(); }
        """;

    [Fact]
    public void SameSourceOnDifferentSettings_MatchesEvenThoughTheBytesDiffer()
    {
        // This is the whole point: a rebuild on another toolchain is not byte-identical, and the
        // comparison has to survive that or it cannot be used at all.
        var a = Compile(Library, "A", Microsoft.CodeAnalysis.OptimizationLevel.Debug);
        var b = Compile(Library, "A2", Microsoft.CodeAnalysis.OptimizationLevel.Release);

        Assert.NotEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
        Assert.True(AssemblySurfaceComparer.Compare(a, b).Matches);
    }

    [Fact]
    public void ARemovedPublicMethod_IsCaught()
    {
        var original = Compile(Library, "B", Microsoft.CodeAnalysis.OptimizationLevel.Debug);
        var rebuilt = Compile(Library.Replace("    public string Greet(string name) => \"hi \" + name;\n", ""), "B2",
            Microsoft.CodeAnalysis.OptimizationLevel.Debug);

        var comparison = AssemblySurfaceComparer.Compare(original, rebuilt);

        Assert.False(comparison.Matches);
        Assert.Contains(comparison.MissingFromRebuild, m => m.Contains("Greet"));
    }

    [Fact]
    public void AChangedSignature_IsCaught()
    {
        var original = Compile(Library, "C", Microsoft.CodeAnalysis.OptimizationLevel.Debug);
        var rebuilt = Compile(Library.Replace("public string Greet(string name)", "public object Greet(string name)"),
            "C2", Microsoft.CodeAnalysis.OptimizationLevel.Debug);

        var comparison = AssemblySurfaceComparer.Compare(original, rebuilt);

        Assert.False(comparison.Matches);
        Assert.Contains(comparison.MissingFromRebuild, m => m.Contains("Greet") && m.Contains("String"));
    }

    [Fact]
    public void AChangedMethodBody_IsNotFlagged()
    {
        // Codegen is exactly what this comparison must ignore; flagging it would make every
        // cross-compiler rebuild look broken.
        var original = Compile(Library, "D", Microsoft.CodeAnalysis.OptimizationLevel.Debug);
        var rebuilt = Compile(Library.Replace("\"hi \" + name", "\"hello \" + name"), "D2",
            Microsoft.CodeAnalysis.OptimizationLevel.Debug);

        Assert.True(AssemblySurfaceComparer.Compare(original, rebuilt).Matches);
    }

    [Fact]
    public void PrivateAndInternalMembers_AreNotPartOfTheSurface()
    {
        var original = Compile(Library, "E", Microsoft.CodeAnalysis.OptimizationLevel.Debug);
        var rebuilt = Compile(
            Library.Replace("internal void Internal() { _hidden++; }", "internal void Renamed() { _hidden--; }"),
            "E2", Microsoft.CodeAnalysis.OptimizationLevel.Debug);

        Assert.True(AssemblySurfaceComparer.Compare(original, rebuilt).Matches);
    }

    [Fact]
    public void Surface_CoversTypesInterfacesPropertiesAndNesting()
    {
        var surface = AssemblySurfaceComparer.ReadSurface(
            Compile(Library, "F", Microsoft.CodeAnalysis.OptimizationLevel.Debug));

        Assert.Contains(surface, s => s.StartsWith("T:Demo.Greeter"));
        Assert.Contains(surface, s => s.StartsWith("T:Demo.IThing") && s.Contains("interface"));
        Assert.Contains(surface, s => s.StartsWith("P:Demo.Greeter.Count"));
        Assert.DoesNotContain(surface, s => s.Contains("_hidden"));
    }

    [Fact]
    public void ReferencedAssemblyVersions_SayWhichBuildOfADependencyTookPart()
    {
        // A PDB records reference file names and MVIDs but no versions, and a nuspec states a
        // range rather than what was resolved. This is the only precise record of which build of
        // a dependency a compilation actually used, and it is what stops reference acquisition
        // fetching the newest release of a package the original built against years ago.
        var versions = AssemblySurfaceComparer.ReadReferencedAssemblyVersions(
            Compile(Library, "H", Microsoft.CodeAnalysis.OptimizationLevel.Debug));

        Assert.NotEmpty(versions);
        Assert.All(versions.Values, v => Assert.True(v.Major >= 0));
        // Lookup is by simple assembly name, which is how a PDB names its references.
        Assert.All(versions.Keys, k => Assert.DoesNotContain(".dll", k));
    }

    [Fact]
    public void ReferencedAssemblyVersions_IsLookedUpByNameCaseInsensitively()
    {
        var versions = AssemblySurfaceComparer.ReadReferencedAssemblyVersions(
            Compile(Library, "I", Microsoft.CodeAnalysis.OptimizationLevel.Debug));
        var name = versions.Keys.First();

        Assert.True(versions.ContainsKey(name.ToUpperInvariant()));
    }

    [Fact]
    public void ReferencedAssemblyIdentities_AreRecordedWithoutTheMvid()
    {
        // Version drift in a dependency makes a rebuild a different library even when its own
        // surface is untouched; a rebuilt dependency at the same version does not.
        var references = AssemblySurfaceComparer.ReadAssemblyReferences(
            Compile(Library, "G", Microsoft.CodeAnalysis.OptimizationLevel.Debug));

        Assert.NotEmpty(references);
        // name, version, public key token - and deliberately no MVID, so a dependency rebuilt at
        // the same identity does not read as a different dependency.
        Assert.All(references, r => Assert.Equal(3, r.Split(", ").Length));
        Assert.All(references, r => Assert.Matches(@"^[^,]+, \d+\.\d+\.\d+\.\d+, (null|[0-9a-f]+)$", r));
    }
}

public class SourceBuildBuildAssetTests
{
    public static TheoryData<string> BuildAssets() =>
    [
        "NuGetToCompLog.SourceBuild.props",
        "NuGetToCompLog.SourceBuild.targets",
    ];

    private static string Find(string fileName)
    {
        for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)))
        {
            var candidate = Path.Combine(dir, "src", "NuGetToCompLog.SourceBuild", "build", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"Could not find {fileName} above {AppContext.BaseDirectory}");
    }

    [Theory]
    [MemberData(nameof(BuildAssets))]
    public void IsLoadableXml(string fileName)
    {
        var document = System.Xml.Linq.XDocument.Load(Find(fileName));

        Assert.Equal("Project", document.Root!.Name.LocalName);
    }

    [Fact]
    public void TheTargetsDoNotRunUnlessTheProjectAsksForIt()
    {
        var text = File.ReadAllText(Find("NuGetToCompLog.SourceBuild.targets"));

        Assert.Contains("AfterTargets=\"ResolvePackageAssets\"", text);
        Assert.Contains("'$(NuGetToCompLogDisableSourceBuild)' != 'true'", text);
        // Restoring reaches the network and runs package code, so it must stay switchable.
        Assert.Contains("'$(NuGetToCompLogAutoBuild)' == 'true'", text);
    }
}

/// <summary>
/// The cache layout is the only contract between the tool that writes an assembly and the MSBuild
/// task that finds it. There is nothing else to keep in step - no lock file, no generated targets -
/// so these tests are what stop the two drifting apart.
/// </summary>
public class CachedAssemblyContractTests : IDisposable
{
    private readonly string _root;

    public CachedAssemblyContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cache-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private async Task<SourceBuildProvenance> StoreAsync(string content = "assembly")
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        var assembly = Path.Combine(staging, "Serilog.dll");
        var complog = Path.Combine(staging, "Serilog.4.0.0.complog");
        await File.WriteAllTextAsync(assembly, content);
        await File.WriteAllTextAsync(complog, "complog");

        return await new SourceBuildCache(Path.Combine(_root, "cache")).StoreAsync(
            "Serilog", "4.0.0", "net8.0", assembly, null, complog, null,
            BinaryEquivalence.ApiEquivalent, [], "4.14.0", false, false, "unconfirmed", 580);
    }

    [Fact]
    public async Task TheTaskFindsWhatTheToolWrote()
    {
        await StoreAsync();

        var cached = CachedAssembly.For(
            Path.Combine(_root, "cache"), "Serilog", "4.0.0", "lib/net8.0/Serilog.dll");

        Assert.NotNull(cached);
        Assert.True(cached.Exists);
        Assert.True(cached.IsIntact());
    }

    [Fact]
    public async Task ARefAssetAndALibAssetOfOnePackageResolveToTheSameBuild()
    {
        // A package shipping a ref/ folder contributes the reference assembly at compile time and
        // the implementation at run time; one source build answers to both.
        await StoreAsync();
        var root = Path.Combine(_root, "cache");

        Assert.Equal(
            CachedAssembly.For(root, "Serilog", "4.0.0", "lib/net8.0/Serilog.dll")!.Path,
            CachedAssembly.For(root, "Serilog", "4.0.0", "ref/net8.0/Serilog.dll")!.Path);
    }

    [Fact]
    public void AnAssetThatWasNeverBuiltIsNotClaimed()
    {
        var root = Path.Combine(_root, "cache");

        // RID-specific assemblies are a different compilation, and must not be replaced by one
        // built for the portable asset.
        Assert.Null(CachedAssembly.For(root, "X", "1.0.0", "runtimes/win-x64/lib/net8.0/X.dll"));
        Assert.Null(CachedAssembly.For(root, "X", "1.0.0", "build/X.dll"));
        Assert.Null(CachedAssembly.For(root, "X", "1.0.0", "analyzers/dotnet/cs/X.dll"));
    }

    [Fact]
    public async Task ATamperedOrHalfWrittenAssemblyIsNotUsed()
    {
        await StoreAsync();
        var cached = CachedAssembly.For(
            Path.Combine(_root, "cache"), "Serilog", "4.0.0", "lib/net8.0/Serilog.dll")!;

        File.WriteAllText(cached.Path, "tampered");

        Assert.True(cached.Exists);
        Assert.False(cached.IsIntact());
    }

    [Fact]
    public void AnAssemblyWithNoProvenanceIsNotTrusted()
    {
        var root = Path.Combine(_root, "cache");
        var cached = CachedAssembly.For(root, "Serilog", "4.0.0", "lib/net8.0/Serilog.dll")!;
        Directory.CreateDirectory(Path.GetDirectoryName(cached.Path)!);
        File.WriteAllText(cached.Path, "not written by the tool");

        Assert.False(cached.IsIntact());
    }
}
