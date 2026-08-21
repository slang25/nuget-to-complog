using NuGetToCompLog.Services;
using Xunit;

namespace NuGetToCompLog.Tests;

public class SourcePathMapperTests
{
    [Fact]
    public void DerivesRootFromPdbPathWithObjSegment()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");

        Assert.Equal("/_/src/Serilog/", mapper.RootPrefix);
    }

    [Fact]
    public void MapsProjectSourcesRelativeToRoot()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");

        Assert.Equal("Capturing/DepthLimiter.cs", mapper.MapToLocal("/_/src/Serilog/Capturing/DepthLimiter.cs"));
    }

    [Fact]
    public void MapsGeneratedObjSourcesUnderRoot()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");

        Assert.Equal(
            "obj/Release/net9.0/Serilog.AssemblyInfo.cs",
            mapper.MapToLocal("/_/src/Serilog/obj/Release/net9.0/Serilog.AssemblyInfo.cs"));
    }

    [Fact]
    public void MapsDocumentsOutsideRootUnderExternal()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");

        Assert.Equal("_external/_/src/Shared/Helpers.cs", mapper.MapToLocal("/_/src/Shared/Helpers.cs"));
        Assert.False(mapper.IsUnderRoot("/_/src/Shared/Helpers.cs"));
    }

    [Fact]
    public void MapsWindowsStylePaths()
    {
        var mapper = SourcePathMapper.Create([], @"C:\repo\src\Lib\obj\Release\net8.0\Lib.pdb");

        Assert.Equal("C:/repo/src/Lib/", mapper.RootPrefix);
        Assert.Equal("Program.cs", mapper.MapToLocal(@"C:\repo\src\Lib\Program.cs"));
    }

    [Fact]
    public void ExternalPathMapRoundTripsUnixRoot()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");
        const string original = "/home/runner/.nuget/packages/x/content/Helpers.cs";

        var maps = SourcePathMapper.DeriveExternalPathMaps([(mapper.MapToLocal(original), original)]);

        var (localPrefix, originalPrefix) = Assert.Single(maps);
        Assert.Equal("_external/", localPrefix);
        Assert.Equal("/", originalPrefix);
        Assert.Equal(original, originalPrefix + mapper.MapToLocal(original)[localPrefix.Length..]);
    }

    [Fact]
    public void ExternalPathMapRoundTripsWindowsDriveRoot()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");
        const string original = @"C:\src\Shared\Helpers.cs";

        var local = mapper.MapToLocal(original);
        Assert.Equal("_external/C/src/Shared/Helpers.cs", local);

        var maps = SourcePathMapper.DeriveExternalPathMaps([(local, original)]);

        var (localPrefix, originalPrefix) = Assert.Single(maps);
        Assert.Equal("_external/C/", localPrefix);
        Assert.Equal("C:/", originalPrefix);
        Assert.Equal("C:/src/Shared/Helpers.cs", originalPrefix + local[localPrefix.Length..]);
    }

    [Fact]
    public void ExternalPathMapEmitsOneEntryPerRootLongestFirst()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");
        string[] originals =
        [
            @"C:\src\Shared\Helpers.cs",
            @"D:\other\Extra.cs",
            "/home/runner/Content.cs",
            @"C:\src\Shared\More.cs",
        ];

        var maps = SourcePathMapper.DeriveExternalPathMaps(
            originals.Select(o => (mapper.MapToLocal(o), o)));

        Assert.Equal(
            [("_external/C/", "C:/"), ("_external/D/", "D:/"), ("_external/", "/")],
            maps);
    }

    [Fact]
    public void ExternalPathMapIgnoresDocumentsUnderTheProjectRoot()
    {
        var mapper = SourcePathMapper.Create([], "/_/src/Serilog/obj/Release/net9.0/Serilog.pdb");
        const string underRoot = "/_/src/Serilog/Capturing/DepthLimiter.cs";

        var maps = SourcePathMapper.DeriveExternalPathMaps([(mapper.MapToLocal(underRoot), underRoot)]);

        Assert.Empty(maps);
    }

    [Fact]
    public void FallsBackToCommonPrefixWithoutPdbPath()
    {
        var mapper = SourcePathMapper.Create(
            ["/repo/src/A/One.cs", "/repo/src/A/Sub/Two.cs", "/repo/src/A/Three.cs"],
            pdbPath: null);

        Assert.Equal("/repo/src/A/", mapper.RootPrefix);
        Assert.Equal("Sub/Two.cs", mapper.MapToLocal("/repo/src/A/Sub/Two.cs"));
    }
}
