using System.Reflection;
using System.Text.RegularExpressions;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Handles the skill command: prints the bundled agent skill (SKILL.md) to stdout, or installs
/// it into an agent's skills directory so coding agents discover the swap workflow on their own.
/// The skill ships embedded in the tool so the installed copy always matches the tool version;
/// the frontmatter version stamp is what makes re-install after a tool update a clean refresh.
/// </summary>
public partial class SkillCommandHandler
{
    public const string SkillName = "swap-nuget-dependency";

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
    /// The tool is the source of truth for the skill, so any copy carrying a different version
    /// stamp is simply replaced. A copy whose stamp matches ours (or is missing) but whose
    /// content differs was edited by hand — that one needs consent to overwrite.
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

        var existingVersion = ReadStampedVersion(existing);
        var currentVersion = ReadStampedVersion(rendered);
        if (existingVersion != null && existingVersion != currentVersion)
        {
            return InstallDecision.Write;
        }

        return InstallDecision.Conflict;
    }

    /// <summary>
    /// Loads the embedded SKILL.md and stamps the current tool version into the frontmatter's
    /// metadata.version, replacing the "dev" placeholder the repository copy carries.
    /// </summary>
    public static string RenderSkill()
    {
        using var stream = typeof(SkillCommandHandler).Assembly.GetManifestResourceStream("SKILL.md")
            ?? throw new InvalidOperationException("Embedded SKILL.md resource not found");
        using var reader = new StreamReader(stream);
        return StampVersion(reader.ReadToEnd(), ToolVersion());
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
        => VersionStampRegex().Replace(content, m => $"{m.Groups[1].Value}{version}", 1);

    public static string? ReadStampedVersion(string content)
    {
        var match = VersionStampRegex().Match(content);
        return match.Success ? match.Groups[2].Value.Trim().Trim('"') : null;
    }

    // Matches the metadata version line in the YAML frontmatter (indented, so it cannot be
    // confused with a top-level key).
    [GeneratedRegex(@"(?m)^(\s+version:\s*)(.*)$")]
    private static partial Regex VersionStampRegex();
}
