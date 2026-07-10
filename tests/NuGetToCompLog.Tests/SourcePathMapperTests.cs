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
    public void FallsBackToCommonPrefixWithoutPdbPath()
    {
        var mapper = SourcePathMapper.Create(
            ["/repo/src/A/One.cs", "/repo/src/A/Sub/Two.cs", "/repo/src/A/Three.cs"],
            pdbPath: null);

        Assert.Equal("/repo/src/A/", mapper.RootPrefix);
        Assert.Equal("Sub/Two.cs", mapper.MapToLocal("/repo/src/A/Sub/Two.cs"));
    }
}
