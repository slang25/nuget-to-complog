using System.Xml.Linq;
using NuGetToCompLog.Services.Swap;
using Xunit;

namespace NuGetToCompLog.Tests;

public class PackageReferenceSwapperTests : IDisposable
{
    private readonly string _tempDir;

    public PackageReferenceSwapperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swapper-tests-{Guid.NewGuid():N}");
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

    private string WriteProject(string content, string name = "App.csproj", string? subdir = null)
    {
        var dir = subdir == null ? _tempDir : Path.Combine(_tempDir, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FindProjectFile_SingleProjectInDirectory()
    {
        var path = WriteProject("<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Equal(path, PackageReferenceSwapper.FindProjectFile(null, _tempDir));
    }

    [Fact]
    public void FindProjectFile_NoProject_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageReferenceSwapper.FindProjectFile(null, _tempDir));
        Assert.Contains("--project", ex.Message);
    }

    [Fact]
    public void FindProjectFile_MultipleProjects_Throws()
    {
        WriteProject("<Project />", "A.csproj");
        WriteProject("<Project />", "B.csproj");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageReferenceSwapper.FindProjectFile(null, _tempDir));
        Assert.Contains("Multiple", ex.Message);
    }

    [Fact]
    public void FindProjectFile_ExplicitRelativePath()
    {
        var path = WriteProject("<Project />", "App.csproj", subdir: "sub");

        Assert.Equal(path, PackageReferenceSwapper.FindProjectFile(Path.Combine("sub", "App.csproj"), _tempDir));
    }

    [Fact]
    public void ResolveVersion_FromVersionAttribute()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "newtonsoft.json");

        Assert.Equal("13.0.3", PackageReferenceSwapper.ResolveVersion(path, references, "newtonsoft.json"));
    }

    [Fact]
    public void ResolveVersion_FromVersionElement()
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
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        Assert.Equal("4.4.0", PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
    }

    [Fact]
    public void ResolveVersion_FromCentralPackageManagement()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """, subdir: "src");
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        Assert.Equal("4.4.0", PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
    }

    [Fact]
    public void ResolveVersion_VersionOverrideWinsOverCpm()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" VersionOverride="4.0.0" />
              </ItemGroup>
            </Project>
            """);
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        Assert.Equal("4.0.0", PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
    }

    [Fact]
    public void ResolveVersion_NothingStated_ReturnsNull()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """);
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        Assert.Null(PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
    }

    [Fact]
    public void Swap_ReplacesPackageReferenceWithProjectReference()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var text = File.ReadAllText(path);
        Assert.Contains("<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />", text);
        Assert.DoesNotContain("PackageReference Include=\"Serilog\"", text);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", text);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", text);
    }

    [Fact]
    public void Swap_PreservesSurroundingFormatting()
    {
        var original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """ + "\n";
        var path = WriteProject(original);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var expected = original.Replace(
            "<PackageReference Include=\"Serilog\" Version=\"4.4.0\" />",
            "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Swap_PreservesCrlfLineEndings()
    {
        var original = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
                       "  <ItemGroup>\r\n" +
                       "    <PackageReference Include=\"Serilog\" Version=\"4.4.0\" />\r\n" +
                       "  </ItemGroup>\r\n" +
                       "</Project>\r\n";
        var path = WriteProject(original);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var text = File.ReadAllText(path);
        var expected = original.Replace(
            "<PackageReference Include=\"Serilog\" Version=\"4.4.0\" />",
            "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />");
        Assert.Equal(expected, text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Swap_PreservesByteOrderMark()
    {
        var original = "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                       "  <ItemGroup>\n" +
                       "    <PackageReference Include=\"Serilog\" Version=\"4.4.0\" />\n" +
                       "  </ItemGroup>\n" +
                       "</Project>\n";
        var path = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes(original))
            .ToArray());

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(
            original.Replace(
                "<PackageReference Include=\"Serilog\" Version=\"4.4.0\" />",
                "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />"),
            System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public void Swap_CarriesOverAssetMetadata()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var doc = XDocument.Load(path);
        var projectReference = doc.Descendants("ProjectReference").Single();
        Assert.Equal("all", (string?)projectReference.Attribute("PrivateAssets"));
        Assert.Null(projectReference.Attribute("Version"));
    }

    [Fact]
    public void Swap_CpmReference_NoVersionAttribute()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.Swap(path, "serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var text = File.ReadAllText(path);
        Assert.Contains("<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />", text);
    }

    [Fact]
    public void ResolveVersion_ConflictingVersions_Throws()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup Condition="'$(TargetFramework)' == 'net472'">
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
        Assert.Contains("3.1.1", ex.Message);
        Assert.Contains("4.4.0", ex.Message);
    }

    [Fact]
    public void ResolveVersion_ConflictingCentralVersions_Throws()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Directory.Packages.props"), """
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net472'">
                <PackageVersion Include="Serilog" Version="3.1.1" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageVersion Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" />
              </ItemGroup>
            </Project>
            """);
        var doc = XDocument.Load(path);
        var references = PackageReferenceSwapper.FindPackageReferences(doc, "Serilog");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageReferenceSwapper.ResolveVersion(path, references, "Serilog"));
        Assert.Contains("3.1.1", ex.Message);
        Assert.Contains("4.4.0", ex.Message);
    }

    [Fact]
    public void Swap_ReplacesEveryConditionalReference()
    {
        var original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net472;net10.0</TargetFrameworks>
              </PropertyGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net472'">
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                <PackageReference Include="Serilog" Version="4.4.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;
        var path = WriteProject(original);

        var replaced = PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        Assert.Equal(2, replaced);
        var expected = original
            .Replace(
                "<PackageReference Include=\"Serilog\" Version=\"4.4.0\" />",
                "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />")
            .Replace(
                "<PackageReference Include=\"Serilog\" Version=\"4.4.0\" PrivateAssets=\"all\" />",
                "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" PrivateAssets=\"all\" />");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Swap_PreservesQuotingEntitiesAndComments()
    {
        var original = """
            <Project Sdk='Microsoft.NET.Sdk'>
              <!-- keep this comment -->
              <PropertyGroup>
                <Description>logging &amp; more</Description>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include='Serilog' Version='4.4.0' PrivateAssets='all'/>
              </ItemGroup>
            </Project>
            """;
        var path = WriteProject(original);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var expected = original.Replace(
            "<PackageReference Include='Serilog' Version='4.4.0' PrivateAssets='all'/>",
            "<ProjectReference Include='patches/Serilog+4.4.0/Serilog.csproj' PrivateAssets='all'/>");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Swap_KeepsChildMetadataAndComments()
    {
        var original = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog">
                  <Version>4.4.0</Version>
                  <!-- not part of our public API -->
                  <PrivateAssets>all</PrivateAssets>
                </PackageReference>
              </ItemGroup>
            </Project>
            """;
        var path = WriteProject(original);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches/Serilog+4.4.0/Serilog.csproj");

        var expected = original
            .Replace(
                "<PackageReference Include=\"Serilog\">",
                "<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\">")
            .Replace("\n      <Version>4.4.0</Version>", "")
            .Replace("</PackageReference>", "</ProjectReference>");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Swap_UnknownPackage_Throws()
    {
        var path = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.4.0" />
              </ItemGroup>
            </Project>
            """);

        Assert.Throws<InvalidOperationException>(
            () => PackageReferenceSwapper.Swap(path, "Newtonsoft.Json", "x.csproj"));
    }

    [Fact]
    public void Swap_LegacyProjectWithXmlNamespace()
    {
        var path = WriteProject("""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PackageReference Include="Serilog">
                  <Version>4.4.0</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        PackageReferenceSwapper.Swap(path, "Serilog", "patches\\Serilog+4.4.0\\Serilog.csproj");

        var text = File.ReadAllText(path);
        Assert.Contains("<ProjectReference Include=\"patches/Serilog+4.4.0/Serilog.csproj\" />", text);
        Assert.Contains("<?xml", text);
        Assert.DoesNotContain("PackageReference", text);
    }
}
