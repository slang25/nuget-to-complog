using NuGetToCompLog.Commands;
using Xunit;

namespace NuGetToCompLog.Tests;

public class SkillCommandHandlerTests
{
    private const string SkillTemplate = """
        ---
        name: swap-nuget-dependency
        description: "Swap a PackageReference for source."
        metadata:
          version: dev
          checksum: dev
        ---

        # Body

        A line mentioning version: 1.2.3 in prose stays untouched.
        ```yaml
          version: 9.9.9
        ```
        """;

    /// <summary>Renders the template the way RenderSkill renders the embedded resource.</summary>
    private static string Render(string version)
    {
        var stamped = SkillCommandHandler.StampVersion(SkillTemplate, version);
        // RenderSkill's checksum stamping is not directly invocable on arbitrary content, but
        // DecideInstall only needs a copy that VerifyChecksum accepts; produce one by the same
        // recipe: hash with the placeholder in place, then substitute.
        return StampChecksum(stamped);
    }

    private static string StampChecksum(string content)
    {
        var digest = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        return content.Replace("checksum: dev", $"checksum: {digest}");
    }

    [Fact]
    public void StampVersion_ReplacesOnlyTheFrontmatterStamp()
    {
        var stamped = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Contains("  version: 0.5.0", stamped);
        Assert.DoesNotContain("version: dev", stamped);
        Assert.Contains("version: 1.2.3 in prose", stamped);
        Assert.Contains("version: 9.9.9", stamped);
    }

    [Fact]
    public void ReadStampedVersion_RoundTripsWithStampVersion()
    {
        var stamped = SkillCommandHandler.StampVersion(SkillTemplate, "0.5.0");

        Assert.Equal("0.5.0", SkillCommandHandler.ReadStampedVersion(stamped));
    }

    [Fact]
    public void ReadStampedVersion_IgnoresVersionLinesOutsideFrontmatter()
    {
        // A hand-written skill with no stamp but an indented version: in a code sample must not
        // be mistaken for a managed copy.
        var handWritten = "---\nname: my-skill\ndescription: mine\n---\n\n```yaml\n  version: 2.0.0\n```\n";

        Assert.Null(SkillCommandHandler.ReadStampedVersion(handWritten));
    }

    [Fact]
    public void VerifyChecksum_AcceptsRenderedCopyAndRejectsEdits()
    {
        var rendered = Render("0.5.0");

        Assert.True(SkillCommandHandler.VerifyChecksum(rendered));
        Assert.False(SkillCommandHandler.VerifyChecksum(rendered + "local tweak\n"));
        Assert.False(SkillCommandHandler.VerifyChecksum(SkillTemplate));
    }

    [Fact]
    public void DecideInstall_WritesWhenTargetMissing()
    {
        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(null, Render("0.5.0"), force: false));
    }

    [Fact]
    public void DecideInstall_UpToDateWhenContentIdentical()
    {
        var rendered = Render("0.5.0");

        Assert.Equal(SkillCommandHandler.InstallDecision.UpToDate,
            SkillCommandHandler.DecideInstall(rendered, rendered, force: false));
    }

    [Fact]
    public void DecideInstall_OverwritesPristineOlderVersionWithoutForce()
    {
        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(Render("0.4.0"), Render("0.5.0"), force: false));
    }

    [Fact]
    public void DecideInstall_ConflictWhenOlderVersionWasEdited()
    {
        // The scenario the version stamp alone cannot catch: an edited copy from an older tool
        // version must conflict, not silently upgrade away the edits.
        var editedOld = Render("0.4.0").Replace("# Body", "# Body\n\nmy local notes");

        Assert.Equal(SkillCommandHandler.InstallDecision.Conflict,
            SkillCommandHandler.DecideInstall(editedOld, Render("0.5.0"), force: false));
        Assert.Equal(SkillCommandHandler.InstallDecision.Write,
            SkillCommandHandler.DecideInstall(editedOld, Render("0.5.0"), force: true));
    }

    [Fact]
    public void DecideInstall_ConflictWhenExistingIsHandWritten()
    {
        Assert.Equal(SkillCommandHandler.InstallDecision.Conflict,
            SkillCommandHandler.DecideInstall("hand-written skill", Render("0.5.0"), force: false));
    }

    [Fact]
    public void RenderSkill_StampsEmbeddedResourceWithVersionAndValidChecksum()
    {
        var rendered = SkillCommandHandler.RenderSkill();

        Assert.StartsWith("---", rendered);
        Assert.Contains("name: swap-nuget-dependency", rendered);
        Assert.Equal(SkillCommandHandler.ToolVersion(), SkillCommandHandler.ReadStampedVersion(rendered));
        Assert.True(SkillCommandHandler.VerifyChecksum(rendered));
    }
}
