using NuGetToCompLog.Services.Generators;
using Xunit;

namespace NuGetToCompLog.Tests;

/// <summary>
/// Reading the generator package version out of the repository's own build files. Text matching
/// only ever saw one of the shapes MSBuild accepts, so a centrally managed version (the common
/// case in modern repos) was missed and the acquisition fell back to guessing recent versions.
/// </summary>
public class PinnedGeneratorVersionTests
{
    private static readonly IReadOnlySet<string> PolySharp =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PolySharp.SourceGenerators", "PolySharp" };

    [Fact]
    public void ReadsInlinePackageReference()
    {
        Assert.Equal("1.14.1", SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="PolySharp" Version="1.14.1" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    [Fact]
    public void ReadsVersionBeforeInclude()
    {
        Assert.Equal("1.14.1", SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <ItemGroup>
                <PackageReference Version="1.14.1" Include="PolySharp" />
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    [Fact]
    public void ReadsCentrallyManagedPackageVersion()
    {
        Assert.Equal("1.15.0", SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="PolySharp" Version="1.15.0" />
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    [Fact]
    public void ReadsVersionFromChildElement()
    {
        Assert.Equal("1.13.0", SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="PolySharp">
                  <Version>1.13.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    [Fact]
    public void ResolvesAPropertyDeclaredInTheSameFile()
    {
        Assert.Equal("1.12.0", SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <PropertyGroup>
                <PolySharpVersion>1.12.0</PolySharpVersion>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="PolySharp" Version="$(PolySharpVersion)" />
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    /// <summary>
    /// A version that stays an unresolved property reference is no use as a version number -
    /// reporting it would pin the acquisition to a package version that cannot exist.
    /// </summary>
    [Fact]
    public void IgnoresAnUnresolvableProperty()
    {
        Assert.Null(SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="PolySharp" Version="$(DefinedElsewhere)" />
              </ItemGroup>
            </Project>
            """, PolySharp));
    }

    [Fact]
    public void CandidateBuildFilesWalkFromTheProjectDirectoryToTheRepoRoot()
    {
        var files = SourceGeneratorAcquisitionService.DeriveCandidateBuildFiles(
            ["/_/src/Foo/obj/Release/net8.0/PolySharp/Type/X.g.cs"], "/_/", "/_/");

        Assert.Equal(
        [
            "src/Foo/Foo.csproj",
            "src/Foo/Directory.Build.props", "src/Foo/Directory.Packages.props",
            "src/Directory.Build.props", "src/Directory.Packages.props",
            "Directory.Build.props", "Directory.Packages.props",
        ], files);
    }

    /// <summary>
    /// A project at the repo root writes its documents straight into obj/, so there is no
    /// directory stretch to read a csproj name from - but the root props files are still
    /// worth probing. This used to throw (a backwards range), which a blanket catch turned
    /// into "no pinned version" for every root-level project.
    /// </summary>
    [Fact]
    public void AProjectAtTheRepoRootStillProbesTheRootPropsFiles()
    {
        var files = SourceGeneratorAcquisitionService.DeriveCandidateBuildFiles(
            ["/_/obj/Release/net8.0/PolySharp/Type/X.g.cs"], "/_/", "/_/");

        Assert.Equal(["Directory.Build.props", "Directory.Packages.props"], files);
    }

    [Fact]
    public void IgnoresOtherPackagesAndUnparseableFiles()
    {
        Assert.Null(SourceGeneratorAcquisitionService.TryReadPinnedVersion(
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Something.Else" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """, PolySharp));
        Assert.Null(SourceGeneratorAcquisitionService.TryReadPinnedVersion("<html>not a project", PolySharp));
    }
}
