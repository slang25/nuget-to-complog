namespace NuGetToCompLog.Domain;

/// <summary>
/// Information about a target framework with priority and version details.
/// </summary>
public record TargetFrameworkInfo(
    string Moniker,
    int Priority,
    int MajorVersion,
    int MinorVersion)
{
    /// <summary>
    /// Creates a TargetFrameworkInfo from a TFM string.
    /// </summary>
    public static TargetFrameworkInfo Parse(string tfm)
    {
        var (priority, major, minor) = GetPriorityAndVersion(tfm);
        return new TargetFrameworkInfo(tfm, priority, major, minor);
    }
    
    private static (int priority, int major, int minor) GetPriorityAndVersion(string tfm)
    {
        var tfmLower = tfm.ToLowerInvariant();
        
        // .NET (net5.0+) - highest priority
        if (tfmLower.StartsWith("net") && !tfmLower.StartsWith("netstandard") && 
            !tfmLower.StartsWith("netcoreapp") && !tfmLower.StartsWith("netframework"))
        {
            var versionPart = tfmLower.Substring(3);
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]) && versionPart[0] >= '5')
            {
                var version = ParseVersion(versionPart);
                return (3, version.major, version.minor);
            }
        }
        
        // .NET Standard - second priority
        if (tfmLower.StartsWith("netstandard"))
        {
            var versionPart = tfmLower.Substring("netstandard".Length);
            var version = ParseVersion(versionPart);
            return (2, version.major, version.minor);
        }
        
        // .NET Core App
        if (tfmLower.StartsWith("netcoreapp"))
        {
            var versionPart = tfmLower.Substring("netcoreapp".Length);
            var version = ParseVersion(versionPart);
            return (2, version.major, version.minor);
        }
        
        // .NET Framework - third priority
        if (tfmLower.StartsWith("net") && (tfmLower.Contains("framework") || char.IsDigit(tfmLower[3])))
        {
            var versionPart = tfmLower.Replace("framework", "");
            if (versionPart.StartsWith("net"))
                versionPart = versionPart.Substring(3);
            var version = ParseVersion(versionPart);
            return (1, version.major, version.minor);
        }
        
        // Unknown - lowest priority
        return (0, 0, 0);
    }
    
    private static (int major, int minor) ParseVersion(string versionPart)
    {
        if (string.IsNullOrEmpty(versionPart))
            return (0, 0);
        
        versionPart = versionPart.TrimStart('v');
        
        var parts = versionPart.Split('.');
        if (parts.Length >= 2)
        {
            if (int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
                return (major, minor);
        }
        else if (parts.Length == 1)
        {
            if (int.TryParse(parts[0], out var version))
            {
                if (version >= 10)
                {
                    var major = version / 10;
                    var minor = version % 10;
                    return (major, minor);
                }
                return (version, 0);
            }
        }
        
        return (0, 0);
    }
}
