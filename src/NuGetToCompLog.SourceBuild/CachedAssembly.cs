using System.Security.Cryptography;
using System.Text;

namespace NuGetToCompLog.SourceBuild;

/// <summary>
/// A source-built assembly in the machine-local cache, and the record the tool wrote beside it.
///
/// The cache layout - &lt;root&gt;/&lt;id&gt;/&lt;version&gt;/&lt;lib tfm&gt;/&lt;assembly&gt;, with the
/// record beside it at &lt;assembly&gt;.provenance.json - is the whole contract between the tool that
/// writes it and this task. Nothing else is shared: there is no lock file to keep in step, because
/// the build already knows the package id, the version its graph resolved and the asset it picked,
/// and passes all three to the tool. The record is named after the assembly because one package and
/// target framework can hold several, each built and recorded separately.
/// </summary>
public sealed class CachedAssembly
{
    public string Path { get; }

    private CachedAssembly(string path) => Path = path;

    /// <summary>
    /// Locates the cached assembly for a resolved package asset. <paramref name="pathInPackage"/>
    /// is a path such as lib/net8.0/Serilog.dll; only lib/ and ref/ assets are eligible, since a
    /// RID-specific assembly under runtimes/ is a different compilation that was never built.
    /// </summary>
    public static CachedAssembly? For(string cacheRoot, string packageId, string version, string pathInPackage)
    {
        var parts = pathInPackage.Replace('\\', '/').Split('/');
        if (parts.Length != 3 || (parts[0] != "lib" && parts[0] != "ref"))
        {
            return null;
        }

        return new CachedAssembly(System.IO.Path.Combine(cacheRoot, packageId, version, parts[1], parts[2]));
    }

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// True when the file on disk is the one the tool recorded caching. Guards against a
    /// truncated file, an interrupted write, or an edit; a build is about to link against it.
    /// Absent provenance means the entry was not written by this tool, which is also a miss.
    /// </summary>
    public bool IsIntact()
    {
        var recorded = RecordedHash();
        return recorded != null && string.Equals(Hash(Path), recorded, StringComparison.OrdinalIgnoreCase);
    }

    private string? RecordedHash()
    {
        var provenance = Path + ".provenance.json";
        if (!File.Exists(provenance))
        {
            return null;
        }

        // One field out of a small file the same tool wrote: cheaper and safer than taking a JSON
        // dependency into a task MSBuild loads into its own assembly graph.
        var text = File.ReadAllText(provenance);
        var key = text.IndexOf("\"sha256\"", StringComparison.OrdinalIgnoreCase);
        if (key < 0)
        {
            return null;
        }
        var open = text.IndexOf('"', text.IndexOf(':', key) + 1);
        var close = open < 0 ? -1 : text.IndexOf('"', open + 1);
        return close < 0 ? null : text.Substring(open + 1, close - open - 1);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var builder = new StringBuilder();
        foreach (var b in sha.ComputeHash(stream))
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }
}
