using System.Xml.Linq;
using XamlG.Syntax;

namespace XamlG.ThemeCorpus;

/// <summary>Includes the pinned project's literal linked XAML items as well as its verified
/// physical corpus. Conditional or unevaluated links are rejected, never silently guessed.</summary>
internal static class ThemeSourceCatalog
{
    public static ThemeSourceInput[] Read(string checkout, string directory, int expectedPhysicalCount)
    {
        var result = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(XamlSourceFile.IsSupported)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Select(path => new ThemeSourceInput(path, Path.GetRelativePath(directory, path).Replace('\\', '/'), false)).ToList();
        if (result.Count != expectedPhysicalCount)
            throw new InvalidOperationException($"Expected {expectedPhysicalCount} pinned physical XAML files, found {result.Count}.");
        var project = Path.Combine(directory, Path.GetFileName(directory) + ".csproj");
        var xml = XDocument.Load(project);
        foreach (var item in xml.Descendants().Where(element => element.Name.LocalName == "AvaloniaResource"))
        {
            var link = (string?)item.Attribute("Link") ?? item.Elements().FirstOrDefault(child => child.Name.LocalName == "Link")?.Value;
            if (link == null) continue;
            var include = (string?)item.Attribute("Include") ?? throw new InvalidDataException("A linked resource has no Include.");
            if (item.AncestorsAndSelf().Any(element => element.Attribute("Condition") != null) ||
                include.IndexOfAny(new[] { '$', '@', '%', '*', '?' }) >= 0 || link.IndexOfAny(new[] { '$', '@', '%', '*', '?' }) >= 0)
                throw new InvalidDataException("The pinned corpus contains a conditional or unevaluated link; evaluate it before accepting this corpus.");
            var source = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(checkout, source).Replace('\\', '/');
            link = link.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(link) || link.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("A linked corpus resource escapes its pinned source or logical root.");
            if (!XamlSourceFile.IsSupported(source) || !File.Exists(source))
                throw new InvalidDataException("The declared linked XAML source is unavailable: " + relative);
            result.Add(new(source, link, true));
        }
        if (result.GroupBy(input => input.LogicalPath, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate logical resource path in the pinned theme project.");
        return result.OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray();
    }
}
