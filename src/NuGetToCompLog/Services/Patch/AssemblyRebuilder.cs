using System.Text.Json;
using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services.SourceBuild;

namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Rebuilds an assembly from patched source files using the compiler response file.
///
/// The compiler is chosen the same way every other rebuild in this tool chooses one: the exact
/// Roslyn the package's PDB recorded, on the runtime that hosted it, falling back to what is
/// installed. A patched rebuild is the original compilation with a few source bytes changed, so
/// everything else about it should still be the original compilation - otherwise a patch that
/// changes nothing produces a different assembly, and there is no way to tell that apart from a
/// patch that changed something.
/// </summary>
public class AssemblyRebuilder
{
    private readonly CscInvoker _csc;
    private readonly IConsoleWriter _console;

    public AssemblyRebuilder(CscInvoker csc, IConsoleWriter console)
    {
        _csc = csc;
        _console = console;
    }

    /// <summary>
    /// Rebuilds an assembly using the build.rsp in the patch directory.
    /// If patchedSourceDir is provided, source paths in the rsp are rewritten to use it.
    /// </summary>
    public async Task<RebuildResult> RebuildAsync(
        string patchDir,
        string? patchedSourceDir = null,
        bool fetchCompiler = false,
        CancellationToken cancellationToken = default)
    {
        var rspPath = Path.Combine(patchDir, "build.rsp");
        if (!File.Exists(rspPath))
        {
            return new RebuildResult(false, "build.rsp not found", null);
        }

        // If we have a separate patched source dir, create a modified rsp
        var effectiveRspPath = rspPath;
        if (patchedSourceDir != null)
        {
            effectiveRspPath = Path.Combine(patchDir, "build.patched.rsp");
            await RewriteRspAsync(rspPath, effectiveRspPath, patchDir, patchedSourceDir);
        }

        var binDir = Path.Combine(patchDir, "bin");
        Directory.CreateDirectory(binDir);

        var (compilerVersion, runtimeVersion) = ReadRecordedToolchain(patchDir);
        var compiler = await _csc.SelectAsync(compilerVersion, runtimeVersion, fetchCompiler, cancellationToken);
        if (compiler == null)
        {
            return new RebuildResult(false, "Could not find csc.dll in any installed .NET SDK", null);
        }

        if (compilerVersion != null && !compiler.CompilerWasExact)
        {
            _console.MarkupLine($"[yellow]⚠[/] Rebuilding with this machine's compiler, not the " +
                                $"{compilerVersion.Split('+')[0]} the package used - the result will differ from " +
                                "the shipped assembly beyond your patch");
        }

        var rspRelative = Path.GetRelativePath(patchDir, effectiveRspPath);
        _console.MarkupLine($"[dim]Running: dotnet exec {{csc}} @{rspRelative}[/]");
        _console.MarkupLine($"[dim]Working directory: {patchDir}[/]");

        try
        {
            var (exitCode, output) = await CscInvoker.RunAsync(compiler, patchDir, rspRelative, cancellationToken);
            if (exitCode != 0)
            {
                return new RebuildResult(false, output.Trim(), null);
            }

            var outputDll = Directory.GetFiles(binDir, "*.dll").FirstOrDefault();
            return new RebuildResult(true, output.Trim(), outputDll);
        }
        catch (Exception ex)
        {
            return new RebuildResult(false, $"Compiler execution failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Reads the compiler and runtime the eject recorded. Directories ejected before those were
    /// written simply have neither, and fall back to whatever is installed.
    /// </summary>
    private static (string? Compiler, string? Runtime) ReadRecordedToolchain(string patchDir)
    {
        var path = Path.Combine(patchDir, "patch-metadata.json");
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<PatchMetadata>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (metadata?.CompilerVersion, metadata?.RuntimeVersion);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private async Task RewriteRspAsync(string originalRsp, string newRsp, string patchDir, string patchedSourceDir)
    {
        var lines = await File.ReadAllLinesAsync(originalRsp);
        var newLines = new List<string>();

        foreach (var line in lines)
        {
            if (TryRewriteSourcePath(line, patchDir, patchedSourceDir, out var rewritten))
            {
                newLines.Add(rewritten);
            }
            else
            {
                newLines.Add(line);
            }
        }

        await File.WriteAllLinesAsync(newRsp, newLines);
    }

    private static bool TryRewriteSourcePath(string line, string patchDir, string patchedSourceDir, out string rewrittenLine)
    {
        rewrittenLine = line;

        var trimmed = line.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('#') || string.IsNullOrEmpty(trimmed))
            return false;

        // Handle optional quotes around paths
        var wasQuoted = trimmed.Length >= 2
            && trimmed.StartsWith('"')
            && trimmed.EndsWith('"');
        var pathValue = wasQuoted ? trimmed[1..^1] : trimmed;

        // Match both src/ and src\ (Windows)
        if (!pathValue.StartsWith("src/", StringComparison.Ordinal) &&
            !pathValue.StartsWith("src\\", StringComparison.Ordinal))
            return false;

        var relativePath = pathValue[4..]
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        var newPath = Path.Combine(patchedSourceDir, relativePath);
        var relativeToDir = Path.GetRelativePath(patchDir, newPath);
        rewrittenLine = wasQuoted ? $"\"{relativeToDir}\"" : relativeToDir;
        return true;
    }



}

public record RebuildResult(bool Success, string Output, string? OutputAssemblyPath);
