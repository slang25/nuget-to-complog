namespace NuGetToCompLog.Services.Patch;

/// <summary>
/// Parses the compiler-arguments.txt the pipeline writes from the PDB compilation-options blob:
/// alternating key/value lines for the recorded option pairs, plus standalone lines starting
/// with '/' for flags that were added verbatim.
/// </summary>
public static class CompilerArgumentsFile
{
    public static (Dictionary<string, string> Args, List<string> ExtraArgs) Parse(string[] lines)
    {
        var dict = new Dictionary<string, string>();
        var extraArgs = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('/'))
            {
                extraArgs.Add(lines[i]);
                continue;
            }

            if (i < lines.Length - 1)
            {
                dict[lines[i]] = lines[i + 1];
                i++;
            }
        }

        return (dict, extraArgs);
    }
}
