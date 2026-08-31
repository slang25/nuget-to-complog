using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the skill command: prints the bundled agent skill (SKILL.md) to stdout, or installs
/// it into an agent's skills directory so coding agents discover the swap workflow on their own.
/// The skill ships embedded in the tool so the installed copy always matches the tool version.
/// Rendered copies carry a version stamp (so a re-install after a tool update reads as a clean
/// refresh) and a content checksum (so a hand-edited copy is recognised as modified no matter
/// which tool version originally installed it).
/// </summary>
public partial class SkillCommandHandler
{
    public const string SkillName = "swap-nuget-dependency";

    private const string StampPlaceholder = "dev";

    private readonly IConsoleWriter _console;

    public SkillCommandHandler(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Agent skill directories, per the Agent Skills convention: user scope under the home
    /// directory, project scope under the current directory. "agents" is the vendor-neutral
    /// .agents/skills location that most other agents scan.
    /// </summary>
    private static readonly Dictionary<string, string> AgentDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ".claude",
        ["codex"] = ".codex",
        ["gemini"] = ".gemini",
        ["agents"] = ".agents",
    };

    public Task<bool> HandleAsync(bool install, bool project, string agent, bool force)
    {
        var content = RenderSkill();

        if (!install)
        {
            // Print mode is for piping and inspection: the skill body is the only stdout output.
            Console.Out.Write(content);
            return Task.FromResult(true);
        }

        if (!AgentDirectories.TryGetValue(agent, out var agentDir))
        {
            _console.MarkupLine($"[red]✗[/] Unknown agent '{agent}'. Expected one of: {string.Join(", ", AgentDirectories.Keys)}");
            return Task.FromResult(false);
        }

        var baseDir = project
            ? Path.Combine(Directory.GetCurrentDirectory(), agentDir, "skills")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), agentDir, "skills");
        var skillDir = Path.Combine(baseDir, SkillName);
        var target = Path.Combine(skillDir, "SKILL.md");

        var decision = DecideInstall(File.Exists(target) ? File.ReadAllText(target) : null, content, force);
        switch (decision)
        {
            case InstallDecision.UpToDate:
                _console.MarkupLine($"[green]✓[/] Skill already up to date at [cyan]{target}[/]");
                return Task.FromResult(true);

            case InstallDecision.Conflict when !Console.IsInputRedirected:
                Console.Error.Write($"The skill at {target} has local modifications. Overwrite? [y/N] ");
                var answer = Console.ReadLine();
                if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                {
                    _console.MarkupLine("[yellow]Aborted; existing skill left untouched.[/]");
                    return Task.FromResult(false);
                }
                break;

            case InstallDecision.Conflict:
                _console.MarkupLine($"[red]✗[/] The skill at [cyan]{target}[/] has local modifications. Re-run with [cyan]--force[/] to overwrite.");
                return Task.FromResult(false);
        }

        Directory.CreateDirectory(skillDir);
        File.WriteAllText(target, content);
        _console.MarkupLine($"[green]✓[/] Installed skill to [cyan]{target}[/]");
        _console.MarkupLine($"[dim]   After updating the tool, re-run 'nuget-to-complog skill --install' to refresh it.[/]");
        return Task.FromResult(true);
    }

    public enum InstallDecision
    {
        Write,
        UpToDate,
        Conflict,
    }

    /// <summary>
    /// The tool is the source of truth for the skill, so a pristine managed copy — one whose
    /// content checksum still verifies, whatever tool version wrote it — is simply replaced.
    /// Anything that fails verification was edited by hand (or written by hand); that needs
    /// consent to overwrite.
    /// </summary>
    public static InstallDecision DecideInstall(string? existing, string rendered, bool force)
    {
        if (existing == null || force)
        {
            return InstallDecision.Write;
        }

        if (existing == rendered)
        {
            return InstallDecision.UpToDate;
        }

        return VerifyChecksum(existing) ? InstallDecision.Write : InstallDecision.Conflict;
    }

    /// <summary>
    /// Loads the embedded SKILL.md, stamps the current tool version into the frontmatter's
    /// metadata.version, then stamps metadata.checksum with the content's SHA-256 (computed
    /// with the checksum field itself still holding the placeholder).
    /// </summary>
    public static string RenderSkill()
    {
        using var stream = typeof(SkillCommandHandler).Assembly.GetManifestResourceStream("SKILL.md")
            ?? throw new InvalidOperationException("Embedded SKILL.md resource not found");
        using var reader = new StreamReader(stream);
        var stamped = StampVersion(reader.ReadToEnd(), ToolVersion());
        return ReplaceInFrontmatter(stamped, ChecksumRegex(), ComputeChecksum(stamped));
    }

    public static string ToolVersion()
    {
        var informational = typeof(SkillCommandHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Strip the SourceLink commit suffix (e.g. "0.4.0+abc123") — the stamp is for humans
        // and version comparison, not provenance.
        return informational?.Split('+')[0] ?? "0.0.0";
    }

    public static string StampVersion(string content, string version)
        => ReplaceInFrontmatter(content, VersionRegex(), version);

    public static string? ReadStampedVersion(string content)
        => ReadFromFrontmatter(content, VersionRegex());

    /// <summary>
    /// True when metadata.checksum matches the SHA-256 of the content with the checksum field
    /// reset to its placeholder — i.e. the file is byte-identical to what some release of the
    /// tool rendered.
    /// </summary>
    public static bool VerifyChecksum(string content)
    {
        var stored = ReadFromFrontmatter(content, ChecksumRegex());
        if (stored == null || stored == StampPlaceholder)
        {
            return false;
        }

        var unstamped = ReplaceInFrontmatter(content, ChecksumRegex(), StampPlaceholder);
        return stored == ComputeChecksum(unstamped);
    }

    private static string ComputeChecksum(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// The YAML frontmatter is the span between the opening "---" and the next line starting
    /// "---". Stamp fields are only ever read or replaced inside it — an indented "version:" in
    /// the Markdown body (a code sample, say) must never be mistaken for the stamp.
    /// </summary>
    private static (int Start, int Length)? FrontmatterBounds(string content)
    {
        if (!content.StartsWith("---\n") && !content.StartsWith("---\r\n"))
        {
            return null;
        }

        var start = content.IndexOf('\n') + 1;
        var end = content.IndexOf("\n---", start, StringComparison.Ordinal);
        return end < 0 ? null : (start, end - start);
    }

    private static string ReplaceInFrontmatter(string content, Regex field, string value)
    {
        if (FrontmatterBounds(content) is not { } bounds)
        {
            return content;
        }

        var (start, length) = bounds;
        var frontmatter = content.Substring(start, length);
        var replaced = field.Replace(frontmatter, m => $"{m.Groups[1].Value}{value}", 1);
        return content[..start] + replaced + content[(start + length)..];
    }

    private static string? ReadFromFrontmatter(string content, Regex field)
    {
        if (FrontmatterBounds(content) is not { } bounds)
        {
            return null;
        }

        var (start, length) = bounds;
        var match = field.Match(content.Substring(start, length));
        return match.Success ? match.Groups[2].Value.Trim().Trim('"') : null;
    }

    [GeneratedRegex(@"(?m)^(\s+version:\s*)(.*)$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?m)^(\s+checksum:\s*)(.*)$")]
    private static partial Regex ChecksumRegex();
}
