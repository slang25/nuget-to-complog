using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NuGetToCompLog.Services.Swap;

/// <summary>
/// Reads and rewrites a consuming project file: locates a PackageReference, resolves the
/// version it pins (directly, via VersionOverride, or via central package management), and
/// swaps the item for a ProjectReference to an ejected package's generated csproj.
///
/// Documents are loaded with whitespace preserved and saved without reformatting so the swap
/// produces a minimal, reviewable diff in the consuming project.
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
    /// Finds the PackageReference item for <paramref name="packageId"/> (case-insensitive,
    /// matching Include or Update). Returns null when the project has no such reference.
    /// </summary>
    public static XElement? FindPackageReference(XDocument project, string packageId)
    {
        return project.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .FirstOrDefault(e => string.Equals(ReferencedId(e), packageId, StringComparison.OrdinalIgnoreCase));
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
    /// item itself (attribute or child element), else the nearest Directory.Packages.props
    /// found walking up from the project directory. Null when nothing states a version.
    /// </summary>
    public static string? ResolveVersion(string projectPath, XElement packageReference, string packageId)
    {
        var version = (string?)packageReference.Attribute("VersionOverride")
            ?? (string?)packageReference.Attribute("Version")
            ?? packageReference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
        if (version != null)
        {
            return version;
        }

        for (var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var packagesProps = Path.Combine(dir, "Directory.Packages.props");
            if (!File.Exists(packagesProps))
            {
                continue;
            }

            var doc = XDocument.Load(packagesProps);
            return doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion")
                .FirstOrDefault(e => string.Equals(ReferencedId(e), packageId, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value;
        }

        return null;
    }

    /// <summary>
    /// Replaces the PackageReference for <paramref name="packageId"/> with a ProjectReference
    /// to <paramref name="projectReferencePath"/> (a path relative to the consuming project).
    /// NuGet asset-control metadata (PrivateAssets, IncludeAssets, ExcludeAssets, Aliases) is
    /// carried over since ProjectReference honours the same metadata.
    /// </summary>
    public static void Swap(string projectPath, string packageId, string projectReferencePath)
    {
        var originalBytes = File.ReadAllBytes(projectPath);
        var doc = XDocument.Load(new MemoryStream(originalBytes), LoadOptions.PreserveWhitespace);

        var packageReference = FindPackageReference(doc, packageId)
            ?? throw new InvalidOperationException($"No PackageReference to {packageId} found in {projectPath}");

        var ns = packageReference.Name.Namespace;
        var projectReference = new XElement(ns + "ProjectReference",
            new XAttribute("Include", projectReferencePath.Replace('\\', '/')));

        foreach (var attribute in packageReference.Attributes())
        {
            if (attribute.Name.LocalName is not ("Include" or "Update" or "Version" or "VersionOverride"))
            {
                projectReference.Add(attribute);
            }
        }
        foreach (var child in packageReference.Elements())
        {
            if (child.Name.LocalName is not ("Version" or "VersionOverride"))
            {
                projectReference.Add(child);
            }
        }
        if (!projectReference.HasElements)
        {
            // Keep <ProjectReference ... /> self-closing rather than <ProjectReference ...></ProjectReference>.
            projectReference.RemoveNodes();
        }

        packageReference.ReplaceWith(projectReference);

        var hadBom = originalBytes.Length >= 3 &&
                     originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = doc.Declaration == null,
            Encoding = new UTF8Encoding(hadBom),
            Indent = false,
        };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, settings))
        {
            doc.Save(writer);
        }

        var text = Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
        var originalText = Encoding.UTF8.GetString(originalBytes);

        // XML parsing normalises every line ending to \n (even under PreserveWhitespace), so a
        // CRLF-authored project would otherwise be rewritten to LF top-to-bottom. Restore the
        // original file's convention so the swap stays a minimal, one-line diff.
        var firstLf = originalText.IndexOf('\n');
        var newline = firstLf > 0 && originalText[firstLf - 1] == '\r' ? "\r\n" : "\n";
        if (newline == "\r\n")
        {
            text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        // XmlWriter drops the conventional trailing newline after the root element.
        if (originalText.EndsWith('\n') && !text.EndsWith('\n'))
        {
            text += newline;
        }
        File.WriteAllText(projectPath, text, settings.Encoding);
    }
}
