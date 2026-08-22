using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NuGetToCompLog.Services.Swap;

/// <summary>
/// Reads and rewrites a consuming project file: locates the PackageReference items for a
/// package, resolves the version they pin (directly, via VersionOverride, or via central
/// package management), and swaps each item for a ProjectReference to an ejected package's
/// generated csproj.
///
/// The rewrite is a span-level splice: the parsed XDocument is used only to *find* the items,
/// and everything outside the matched elements is copied through byte-for-byte. Re-serialising
/// the document would lose the file's lexical details (attribute quoting, entity spellings,
/// comments, whitespace, line endings) and turn a one-line swap into a whole-file diff.
/// </summary>
public static class PackageReferenceSwapper
{
    /// <summary>
    /// Resolves which project file to operate on. <paramref name="projectOption"/> may name a
    /// file or a directory; when null, the working directory must contain exactly one .csproj.
    /// Throws with a user-facing message otherwise.
    /// </summary>
    public static string FindProjectFile(string? projectOption, string workingDirectory)
    {
        if (projectOption != null)
        {
            var path = Path.GetFullPath(projectOption, workingDirectory);
            if (File.Exists(path))
            {
                return path;
            }
            if (Directory.Exists(path))
            {
                return SingleProjectIn(path);
            }
            throw new InvalidOperationException($"Project not found: {projectOption}");
        }

        return SingleProjectIn(workingDirectory);
    }

    private static string SingleProjectIn(string directory)
    {
        var projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new InvalidOperationException(
                $"No .csproj found in {directory}. Pass --project to point at the consuming project."),
            _ => throw new InvalidOperationException(
                $"Multiple .csproj files in {directory}. Pass --project to pick one."),
        };
    }

    /// <summary>
    /// Finds every PackageReference item for <paramref name="packageId"/> (case-insensitive,
    /// matching Include or Update). Multi-targeting projects routinely reference the same
    /// package from several conditional ItemGroups, and all of them have to be swapped or the
    /// remaining target frameworks would still consume the package from the feed.
    /// </summary>
    public static IReadOnlyList<XElement> FindPackageReferences(XDocument project, string packageId)
    {
        return project.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Where(e => string.Equals(ReferencedId(e), packageId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static List<string> ListPackageReferences(XDocument project)
    {
        return project.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(ReferencedId)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReferencedId(XElement packageReference) =>
        (string?)packageReference.Attribute("Include") ?? (string?)packageReference.Attribute("Update");

    /// <summary>
    /// Resolves the version a project pins for a package: VersionOverride or Version on the
    /// items themselves (attribute or child element), else the nearest Directory.Packages.props
    /// found walking up from the project directory. Null when nothing states a version. Throws
    /// when the project pins more than one version for the package, since picking one silently
    /// would eject a version half the target frameworks never used.
    /// </summary>
    public static string? ResolveVersion(
        string projectPath,
        IReadOnlyList<XElement> packageReferences,
        string packageId)
    {
        var stated = packageReferences
            .Select(StatedVersion)
            .Where(v => v != null)
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (stated.Count > 0)
        {
            return OnlyVersion(stated, $"The PackageReference items for {packageId} in {Path.GetFileName(projectPath)}");
        }

        for (var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var packagesProps = Path.Combine(dir, "Directory.Packages.props");
            if (!File.Exists(packagesProps))
            {
                continue;
            }

            var doc = XDocument.Load(packagesProps);
            var versions = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion")
                .Where(e => string.Equals(ReferencedId(e), packageId, StringComparison.OrdinalIgnoreCase))
                .Select(e => (string?)e.Attribute("Version"))
                .Where(v => v != null)
                .Select(v => v!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return versions.Count == 0
                ? null
                : OnlyVersion(versions, $"The PackageVersion items for {packageId} in {packagesProps}");
        }

        return null;

        static string? StatedVersion(XElement packageReference) =>
            (string?)packageReference.Attribute("VersionOverride")
            ?? (string?)packageReference.Attribute("Version")
            ?? packageReference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

        static string OnlyVersion(List<string> versions, string subject) => versions.Count == 1
            ? versions[0]
            : throw new InvalidOperationException(
                $"{subject} pin different versions ({string.Join(", ", versions)}). " +
                "Pass the version explicitly to pick one.");
    }

    /// <summary>
    /// Replaces every PackageReference for <paramref name="packageId"/> with a ProjectReference
    /// to <paramref name="projectReferencePath"/> (a path relative to the consuming project) and
    /// returns how many items were replaced. NuGet asset-control metadata (PrivateAssets,
    /// IncludeAssets, ExcludeAssets, Aliases) is carried over since ProjectReference honours the
    /// same metadata. Only the matched elements are rewritten; the rest of the file is untouched.
    /// </summary>
    public static int Swap(string projectPath, string packageId, string projectReferencePath)
    {
        var originalBytes = File.ReadAllBytes(projectPath);
        var hadBom = originalBytes.Length >= 3 &&
                     originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(originalBytes).TrimStart('\uFEFF');

        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var packageReferences = FindPackageReferences(doc, packageId);
        if (packageReferences.Count == 0)
        {
            throw new InvalidOperationException($"No PackageReference to {packageId} found in {projectPath}");
        }

        var lineStarts = LineStarts(text);
        var spans = packageReferences
            .Select(e => ElementSpan(text, lineStarts, e))
            .OrderByDescending(span => span.Start)
            .ToList();

        var rewritten = new StringBuilder(text);
        foreach (var (start, end) in spans)
        {
            var replacement = RewriteItem(text[start..end], projectReferencePath);
            rewritten.Remove(start, end - start).Insert(start, replacement);
        }

        File.WriteAllText(projectPath, rewritten.ToString(), new UTF8Encoding(hadBom));
        return packageReferences.Count;
    }

    /// <summary>
    /// Offsets of the first character of each line, counting line breaks the way XmlReader does
    /// (\r\n, \n and a lone \r each end one line) so its reported positions map back to the text.
    /// </summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    /// <summary>Character range of an element's full text, from its '&lt;' to the '&gt;' that closes it.</summary>
    private static (int Start, int End) ElementSpan(string text, List<int> lineStarts, XElement element)
    {
        var lineInfo = (IXmlLineInfo)element;
        if (!lineInfo.HasLineInfo() || lineInfo.LineNumber > lineStarts.Count)
        {
            throw new InvalidOperationException(
                $"Could not locate the <{element.Name.LocalName}> item in the project file.");
        }

        // LinePosition points at the element name, one past the '<' that opens the tag.
        var start = lineStarts[lineInfo.LineNumber - 1] + lineInfo.LinePosition - 2;
        if (start < 0 || text[start] != '<')
        {
            throw new InvalidOperationException(
                $"Could not locate the <{element.Name.LocalName}> item in the project file.");
        }

        return (start, FindElementEnd(text, start));
    }

    /// <summary>
    /// Rewrites one PackageReference item's text into the equivalent ProjectReference, editing
    /// the start tag in place so unrelated attributes, child elements and comments survive
    /// verbatim. Version/VersionOverride are dropped (a project reference has no version) and
    /// Include/Update becomes the project path.
    /// </summary>
    private static string RewriteItem(string element, string projectReferencePath)
    {
        var startTag = ScanTag(element, 0);
        var prefix = Prefix(startTag.Name);
        var rewritten = new StringBuilder("<").Append(prefix).Append("ProjectReference");

        var i = 1 + startTag.Name.Length;
        string tail;
        while (true)
        {
            var separatorStart = i;
            while (i < element.Length && char.IsWhiteSpace(element[i]))
            {
                i++;
            }
            if (i >= element.Length || element[i] is '/' or '>')
            {
                tail = element[separatorStart..startTag.End];
                break;
            }

            var nameStart = i;
            while (element[i] != '=' && !char.IsWhiteSpace(element[i]))
            {
                i++;
            }
            var name = element[nameStart..i];
            while (char.IsWhiteSpace(element[i]) || element[i] == '=')
            {
                i++;
            }
            var quote = element[i];
            i = element.IndexOf(quote, i + 1) + 1;

            switch (LocalName(name))
            {
                case "Include" or "Update":
                    rewritten.Append(element[separatorStart..nameStart])
                        .Append(Prefix(name)).Append("Include=").Append(quote)
                        .Append(EscapeAttributeValue(projectReferencePath.Replace('\\', '/'), quote))
                        .Append(quote);
                    break;
                case "Version" or "VersionOverride":
                    // Dropped along with the whitespace that separated it from the previous attribute.
                    break;
                default:
                    rewritten.Append(element[separatorStart..i]);
                    break;
            }
        }

        var content = startTag.IsSelfClosing
            ? string.Empty
            : StripVersionChildren(element[startTag.End..element.LastIndexOf("</", StringComparison.Ordinal)]);
        if (content.Trim().Length == 0)
        {
            // Keep the item self-closing rather than <ProjectReference ...></ProjectReference>.
            var separator = tail[..^1];
            return rewritten
                .Append(startTag.IsSelfClosing ? tail : separator.Length == 0 ? " />" : separator + "/>")
                .ToString();
        }

        return rewritten.Append(tail).Append(content).Append("</").Append(prefix).Append("ProjectReference>").ToString();
    }

    /// <summary>
    /// Removes the Version/VersionOverride child elements from an item's content, taking the
    /// whitespace that indented them with each one so no blank line is left behind.
    /// </summary>
    private static string StripVersionChildren(string content)
    {
        var kept = new StringBuilder();
        var copiedTo = 0;
        var i = 0;
        while (i < content.Length)
        {
            if (content[i] != '<')
            {
                i++;
                continue;
            }
            if (SkipUnparsed(content, ref i))
            {
                continue;
            }

            var child = ScanTag(content, i);
            var end = FindElementEnd(content, i);
            if (LocalName(child.Name) is "Version" or "VersionOverride")
            {
                var indentStart = i;
                while (indentStart > copiedTo && char.IsWhiteSpace(content[indentStart - 1]))
                {
                    indentStart--;
                }
                kept.Append(content, copiedTo, indentStart - copiedTo);
                copiedTo = end;
            }
            i = end;
        }

        return kept.Append(content, copiedTo, content.Length - copiedTo).ToString();
    }

    /// <summary>End offset (exclusive) of the element starting at <paramref name="start"/>.</summary>
    private static int FindElementEnd(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                i++;
                continue;
            }
            if (SkipUnparsed(text, ref i))
            {
                continue;
            }

            var tag = ScanTag(text, i);
            i = tag.End;
            if (tag.IsEndTag)
            {
                depth--;
            }
            else if (!tag.IsSelfClosing)
            {
                depth++;
            }
            if (depth <= 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Unterminated element in the project file.");
    }

    /// <summary>
    /// Advances past a comment, CDATA section or processing instruction at <paramref name="i"/>,
    /// whose contents can hold anything that would otherwise look like markup.
    /// </summary>
    private static bool SkipUnparsed(string text, ref int i)
    {
        var (opening, closing) = text.AsSpan(i) switch
        {
            var s when s.StartsWith("<!--") => ("<!--", "-->"),
            var s when s.StartsWith("<![CDATA[") => ("<![CDATA[", "]]>"),
            var s when s.StartsWith("<?") => ("<?", "?>"),
            _ => (null, null),
        };
        if (opening == null)
        {
            return false;
        }

        var end = text.IndexOf(closing!, i + opening.Length, StringComparison.Ordinal);
        i = end < 0 ? text.Length : end + closing!.Length;
        return true;
    }

    private readonly record struct Tag(int End, string Name, bool IsEndTag, bool IsSelfClosing);

    /// <summary>Scans the tag starting at <paramref name="start"/>, skipping over quoted attribute values.</summary>
    private static Tag ScanTag(string text, int start)
    {
        var isEndTag = start + 1 < text.Length && text[start + 1] == '/';
        var nameStart = start + (isEndTag ? 2 : 1);
        var nameEnd = nameStart;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('/' or '>'))
        {
            nameEnd++;
        }

        var quote = '\0';
        var i = nameEnd;
        for (; i < text.Length; i++)
        {
            if (quote != '\0')
            {
                if (text[i] == quote)
                {
                    quote = '\0';
                }
            }
            else if (text[i] is '"' or '\'')
            {
                quote = text[i];
            }
            else if (text[i] == '>')
            {
                break;
            }
        }
        if (i >= text.Length)
        {
            throw new InvalidOperationException("Unterminated tag in the project file.");
        }

        return new Tag(i + 1, text[nameStart..nameEnd], isEndTag, text[i - 1] == '/');
    }

    private static string Prefix(string qualifiedName)
    {
        var colon = qualifiedName.IndexOf(':');
        return colon < 0 ? string.Empty : qualifiedName[..(colon + 1)];
    }

    private static string LocalName(string qualifiedName) =>
        qualifiedName[(qualifiedName.IndexOf(':') + 1)..];

    private static string EscapeAttributeValue(string value, char quote) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(quote.ToString(), quote == '"' ? "&quot;" : "&apos;");
}
