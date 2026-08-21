namespace NuGetToCompLog.Services;

/// <summary>
/// Maps the PDB compilation-options "optimization" value to csc flags. Roslyn serializes the
/// optimization level as "debug" or "release", with a "-debug-plus" suffix when the original
/// build passed /debug+ (DebugPlusMode). Treating the whole string as the level makes
/// "release-debug-plus" unrecognized and silently flips the rebuild to /optimize-.
/// </summary>
public static class OptimizationOptions
{
    public static IReadOnlyList<string> ToCompilerFlags(string optimization)
    {
        const string debugPlusSuffix = "-debug-plus";
        var debugPlus = optimization.EndsWith(debugPlusSuffix, StringComparison.OrdinalIgnoreCase);
        var level = debugPlus ? optimization[..^debugPlusSuffix.Length] : optimization;
        var optimize = level.Equals("release", StringComparison.OrdinalIgnoreCase) ||
                       level.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                       level.Equals("1", StringComparison.OrdinalIgnoreCase);

        var flags = new List<string>();
        if (debugPlus)
        {
            flags.Add("/debug+");
        }
        flags.Add($"/optimize{(optimize ? "+" : "-")}");
        return flags;
    }
}
