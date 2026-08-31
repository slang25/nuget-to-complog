using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Commands;

namespace NuGetToCompLog.Cli;

/// <summary>
/// CLI commands for NuGet to CompLog tool.
/// Commands are automatically discovered and wired up by ConsoleAppFramework.
/// </summary>
public class NuGetCommands
{
    private readonly ProcessPackageCommandHandler _processHandler;
    private readonly EjectPackageCommandHandler _ejectHandler;
    private readonly SwapCommandHandler _swapHandler;
    private readonly SkillCommandHandler _skillHandler;
    private readonly DiffCommandHandler _diffHandler;
    private readonly ApplyCommandHandler _applyHandler;
    private readonly VerifyCommandHandler _verifyHandler;
    private readonly SourceBuildCommandHandler _sourceBuildHandler;
    private readonly IConsoleWriter _console;

    /// <summary>
    /// Whether to keep package-controlled generator code out of this process. Proving a
    /// generator regenerates the recorded documents means running it here, so the switch also
    /// exists as an environment variable for unattended runs that never see a command line.
    /// </summary>
    private static bool SkipGenerators(bool flag) =>
        flag || Environment.GetEnvironmentVariable("NUGET_TO_COMPLOG_SKIP_GENERATORS") is "1" or "true";

    public NuGetCommands(
        ProcessPackageCommandHandler processHandler,
        EjectPackageCommandHandler ejectHandler,
        SwapCommandHandler swapHandler,
        SkillCommandHandler skillHandler,
        DiffCommandHandler diffHandler,
        ApplyCommandHandler applyHandler,
        VerifyCommandHandler verifyHandler,
        SourceBuildCommandHandler sourceBuildHandler,
        IConsoleWriter console)
    {
        _processHandler = processHandler;
        _ejectHandler = ejectHandler;
        _swapHandler = swapHandler;
        _skillHandler = skillHandler;
        _diffHandler = diffHandler;
        _applyHandler = applyHandler;
        _verifyHandler = verifyHandler;
        _sourceBuildHandler = sourceBuildHandler;
        _console = console;
    }

    /// <summary>
    /// Process a NuGet package and create a CompLog file.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Newtonsoft.Json)</param>
    /// <param name="version">The package version (e.g., 13.0.3). If not specified, uses latest stable version.</param>
    /// <param name="assembly">Which assembly to capture when the package ships several for one target framework (e.g. nunit.framework.dll). Defaults to the one named after the package.</param>
    /// <param name="skipGenerators">Do not load or run source generator assemblies in this process. Their documents are then passed as plain source files, which cannot reproduce the original PDB exactly.</param>
    [Command("")]
    public async Task Process(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? assembly = null,
        bool skipGenerators = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var command = new ProcessPackageCommand(
                packageId, version, assembly, RunGenerators: !SkipGenerators(skipGenerators));
            var result = await _processHandler.HandleAsync(command);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Verify a package round-trips: create a complog, rebuild from it with the exact compiler,
    /// and byte-compare the result against the assembly shipped in the package.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Serilog)</param>
    /// <param name="version">The package version. If not specified, uses latest stable version.</param>
    /// <param name="fetchCompiler">Download the exact compiler (Microsoft.Net.Compilers.Toolset) from nuget.org or the dnceng dotnet-tools feed when it is not installed locally.</param>
    /// <param name="assembly">Which assembly to verify when the package ships several for one target framework (e.g. nunit.framework.legacy.dll). Defaults to the one named after the package.</param>
    /// <param name="skipGenerators">Do not load or run source generator assemblies in this process. Their documents are then passed as plain source files, which cannot reproduce the original PDB exactly.</param>
    [Command("verify")]
    public async Task Verify(
        [Argument] string packageId,
        [Argument] string? version = null,
        bool fetchCompiler = false,
        string? assembly = null,
        bool skipGenerators = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            Environment.ExitCode = await _verifyHandler.HandleAsync(
                packageId, version, fetchCompiler, assembly, runGenerators: !SkipGenerators(skipGenerators));
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Create a patch from changes made to an ejected package.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier</param>
    /// <param name="version">The package version (auto-detected if only one version is ejected)</param>
    /// <param name="patchesDir">Base directory for patches. Defaults to ./patches/</param>
    [Command("diff")]
    public async Task Diff(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? patchesDir = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _diffHandler.HandleAsync(packageId, version, patchesDir);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Apply patches and rebuild assemblies.
    /// </summary>
    /// <param name="packageId">Optional: apply only patches for this package</param>
    /// <param name="patchesDir">Base directory for patches. Defaults to ./patches/</param>
    /// <param name="fetchCompiler">Download the exact compiler the package was built with (Microsoft.Net.Compilers.Toolset) when it is not installed locally, so the rebuild differs from the shipped assembly only by your patch.</param>
    [Command("apply")]
    public async Task Apply(
        [Argument] string? packageId = null,
        string? patchesDir = null,
        bool fetchCompiler = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _applyHandler.HandleAsync(packageId, patchesDir, fetchCompiler: fetchCompiler);

            if (!result)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Print a bundled agent skill (SKILL.md) to stdout, or install the bundled skills into an
    /// agent's skills directory so it knows these workflows exist.
    /// </summary>
    /// <param name="install">Install the skills instead of printing one to stdout. Installs all of them unless --name says otherwise.</param>
    /// <param name="name">Which skill: swap-nuget-dependency (change a dependency) or source-build-nuget-package (build it from source unchanged). Defaults to printing swap-nuget-dependency, and to installing both.</param>
    /// <param name="project">Install to the current directory's project-level skills folder (e.g. ./.claude/skills/) instead of the user-level one.</param>
    /// <param name="agent">Which agent's skills directory to install to: claude, codex, gemini, or agents (the vendor-neutral .agents/skills).</param>
    /// <param name="force">Overwrite an installed skill even if it has local modifications.</param>
    [Command("skill")]
    public async Task Skill(
        bool install = false,
        string? name = null,
        bool project = false,
        string agent = "claude",
        bool force = false)
    {
        var result = await _skillHandler.HandleAsync(install, project, agent, force, name);
        if (!result)
        {
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Swap a project's PackageReference for the package built from its recovered source:
    /// ejects the package into an editable project with a generated .csproj and rewrites the
    /// consuming project to use a ProjectReference instead.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier of the PackageReference to swap</param>
    /// <param name="version">The package version. If not specified, resolved from the project or Directory.Packages.props.</param>
    /// <param name="project">Path to the consuming project (.csproj) or its directory. Defaults to the single .csproj in the current directory.</param>
    /// <param name="output">Output directory for the ejected project. Defaults to ./patches/</param>
    /// <param name="assembly">Which assembly to eject when the package ships several for one target framework. Defaults to the one named after the package.</param>
    [Command("swap")]
    public async Task Swap(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? project = null,
        string? output = null,
        string? assembly = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _swapHandler.HandleAsync(packageId, version, project, output, assembly);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }


    /// <summary>
    /// Mark a project's PackageReference so its next build uses an assembly compiled from the
    /// package's own source instead of the one the package shipped. No source code is written
    /// into the repository - it stays inside a complog in a machine-local cache.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier of the PackageReference to mark</param>
    /// <param name="assets">Build these assets and cache them, instead of marking a project. Each is &lt;packageId&gt;/&lt;version&gt;/&lt;pathInPackage&gt;, separated by semicolons. This is how the MSBuild targets invoke the tool; a build already knows which assets it resolved.</param>
    /// <param name="project">Path to the project (.csproj) or its directory. Defaults to the single .csproj in the current directory.</param>
    /// <param name="fetchCompiler">Download the exact compiler (Microsoft.Net.Compilers.Toolset) from nuget.org or the dnceng dotnet-tools feed when it is not installed locally.</param>
    /// <param name="allowDivergent">Accept a rebuild that is not the same library as the shipped assembly. The substituted assembly is then not known to behave like the one it replaces.</param>
    /// <param name="skipGenerators">Do not load or run source generator assemblies in this process. Their documents are then passed as plain source files, which cannot reproduce the original PDB exactly.</param>
    [Command("sourcebuild")]
    public async Task SourceBuild(
        [Argument] string? packageId = null,
        string? assets = null,
        string? project = null,
        bool fetchCompiler = false,
        bool allowDivergent = false,
        bool skipGenerators = false)
    {
        _console.SetIndeterminateProgress();
        try
        {
            Environment.ExitCode = await _sourceBuildHandler.HandleAsync(
                packageId, assets, project, fetchCompiler, allowDivergent,
                runGenerators: !SkipGenerators(skipGenerators));
        }
        finally
        {
            _console.ClearProgress();
        }
    }

    /// <summary>
    /// Eject a NuGet package into an editable source project.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier (e.g., Newtonsoft.Json)</param>
    /// <param name="version">The package version. If not specified, uses latest stable version.</param>
    /// <param name="output">Output directory for patches. Defaults to ./patches/</param>
    /// <param name="assembly">Which assembly to eject when the package ships several for one target framework. Defaults to the one named after the package.</param>
    [Command("eject")]
    public async Task Eject(
        [Argument] string packageId,
        [Argument] string? version = null,
        string? output = null,
        string? assembly = null)
    {
        _console.SetIndeterminateProgress();
        try
        {
            var result = await _ejectHandler.HandleAsync(packageId, version, output, assembly);

            if (result == null)
            {
                Environment.ExitCode = 1;
                return;
            }
        }
        finally
        {
            _console.ClearProgress();
        }
    }
}
