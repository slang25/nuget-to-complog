using System.Diagnostics;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Rebuilds an assembly from patched source files using the compiler response file.
/// </summary>
public class AssemblyRebuilder
{
    private readonly IConsoleWriter _console;

    public AssemblyRebuilder(IConsoleWriter console)
    {
        _console = console;
    }

    /// <summary>
    /// Rebuilds an assembly using the build.rsp in the patch directory.
    /// If patchedSourceDir is provided, source paths in the rsp are rewritten to use it.
    /// </summary>
    public async Task<RebuildResult> RebuildAsync(
        string patchDir,
        string? patchedSourceDir = null,
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

        // Find dotnet executable
        var dotnetPath = FindDotnet();
        if (dotnetPath == null)
        {
            return new RebuildResult(false, "Could not find 'dotnet' on PATH", null);
        }

        // Find csc.dll
        var cscPath = FindCscDll();
        if (cscPath == null)
        {
            return new RebuildResult(false, "Could not find csc.dll in .NET SDK", null);
        }

        var rspRelative = Path.GetRelativePath(patchDir, effectiveRspPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            WorkingDirectory = patchDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cscPath);
        startInfo.ArgumentList.Add($"@{rspRelative}");

        _console.MarkupLine($"[dim]Running: dotnet exec {{csc}} @{rspRelative}[/]");
        _console.MarkupLine($"[dim]Working directory: {patchDir}[/]");

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new RebuildResult(false, "Failed to start compiler process", null);
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var output = (stdout + "\n" + stderr).Trim();

            if (process.ExitCode == 0)
            {
                // Find the output assembly
                var outputDll = Directory.GetFiles(binDir, "*.dll").FirstOrDefault();
                return new RebuildResult(true, output, outputDll);
            }
            else
            {
                return new RebuildResult(false, output, null);
            }
        }
        catch (Exception ex)
        {
            return new RebuildResult(false, $"Compiler execution failed: {ex.Message}", null);
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

    private static string? FindDotnet()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot != null)
        {
            var dotnetExe = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(dotnetExe))
                return dotnetExe;
        }

        // Try common locations
        var candidates = new[]
        {
            "/usr/local/share/dotnet/dotnet",
            "/usr/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to PATH
        return "dotnet";
    }

    private static string? FindCscDll()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
                : "/usr/local/share/dotnet");

        var sdkPath = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkPath))
            return null;

        var latestSdk = Directory.GetDirectories(sdkPath)
            .Select(d => (Path: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Path)
            .FirstOrDefault();

        if (latestSdk == null)
            return null;

        var cscPath = Path.Combine(latestSdk, "Roslyn", "bincore", "csc.dll");
        return File.Exists(cscPath) ? cscPath : null;
    }

    private static Version? ParseVersion(string name)
    {
        // SDK directory names like "10.0.100" or "9.0.200-preview.1"
        var dashIndex = name.IndexOf('-');
        var versionPart = dashIndex >= 0 ? name[..dashIndex] : name;
        return Version.TryParse(versionPart, out var v) ? v : null;
    }
}

public record RebuildResult(bool Success, string Output, string? OutputAssemblyPath);
