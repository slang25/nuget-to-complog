using NuGetToCompLog.Commands;
using Xunit;

namespace NuGetToCompLog.Tests;

public class SkillCommandHandlerTests
{
    private const string SkillTemplate = """
        ---
        name: swap-nuget-dependency
        description: Swap a PackageReference for source.
        metadata:
          version: dev
          source: nugettocomplog
        ---

        # Body

        A line mentioning version: 1.2.3 in prose stays untouched.
        """;

    [Fact]
    public void StampVersion_ReplacesOnlyTheFrontmatterStamp()
    {
        var stamped = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Contains("  version: 0.5.0", stamped);
        Assert.DoesNotContain("version: dev", stamped);
        Assert.Contains("version: 1.2.3 in prose", stamped);
    }

    [Fact]
    public void ReadStampedVersion_RoundTripsWithStampVersion()
    {
        var stamped = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal("0.5.0", SkillCommandHandler.ReadStampedVersion(stamped));
    }

    [Fact]
    public void ReadStampedVersion_ReturnsNullWhenNoStamp()
    {
        Assert.Null(SkillCommandHandler.ReadStampedVersion("---\nname: x\n---\nbody"));
    }

    [Fact]
    public void DecideInstall_WritesWhenTargetMissing()
    {
        var rendered = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(null, rendered, force: false));
    }

    [Fact]
    public void DecideInstall_UpToDateWhenContentIdentical()
    {
        var rendered = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal(SkillCommandHandler.InstallDecision.UpToDate,
            SkillCommandHandler.DecideInstall(rendered, rendered, force: false));
    }

    [Fact]
    public void DecideInstall_OverwritesOlderVersionWithoutForce()
    {
        var old = SkillCommandHandler.StampVersion(SkillTemplate, "0.4.0");
        var rendered = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(old, rendered, force: false));
    }

    [Fact]
    public void DecideInstall_ConflictWhenSameVersionButEdited()
    {
        var rendered = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");
        var edited = rendered + "\nlocal tweak\n";

        Assert.Equal(SkillCommandHandler.InstallDecision.Conflict,
            SkillCommandHandler.DecideInstall(edited, rendered, force: false));
        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(edited, rendered, force: true));
    }

    [Fact]
    public void DecideInstall_ConflictWhenExistingHasNoStamp()
    {
        var rendered = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal(SkillCommandHandler.InstallDecision.Conflict,
            SkillCommandHandler.DecideInstall("hand-written skill", rendered, force: false));
    }

    [Fact]
    public void RenderSkill_StampsEmbeddedResourceWithToolVersion()
    {
        var rendered = SkillCommandHandler.RenderSkill();

        Assert.StartsWith("---", rendered);
        Assert.Contains("name: swap-nuget-dependency", rendered);
        Assert.Equal(SkillCommandHandler.ToolVersion(), SkillCommandHandler.ReadStampedVersion(rendered));
    }
}
