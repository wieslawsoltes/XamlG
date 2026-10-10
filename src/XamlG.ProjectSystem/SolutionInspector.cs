using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace XamlG.ProjectSystem;

public sealed record ProjectItemDescription(string Kind, string Include, string? Remove, string? Update,
    string? Exclude, string? Link, string? Condition, string? Version);
public sealed record ProjectDescription(string Path, string Name, string Language, string Sdk,
    ImmutableDictionary<string, string> Properties, ImmutableArray<string> TargetFrameworks,
    ImmutableArray<string> ProjectReferences, ImmutableArray<ProjectItemDescription> Items,
    ImmutableArray<string> Diagnostics);
public sealed record SolutionProject(string Path, string Name, string? Folder, bool Loaded, ProjectDescription? Definition);
public sealed record SolutionDescription(string? EntryPath, string Format, ImmutableArray<SolutionProject> Projects,
    ImmutableArray<string> Folders, ImmutableArray<string> SolutionItems, ImmutableArray<string> Diagnostics)
{
    public bool Evaluated => false;
}

/// <summary>Structural inspection only. Never executes imports, SDK resolvers, targets or property functions.</summary>
public static class SolutionInspector
{
    public const string StructuralNotice = "Structural inspection only: conditions, imports, SDKs, globs and package assets require trusted MSBuild evaluation.";
    private static readonly Regex SlnProject = new(
        "^Project\\(\"(?<type>[^\"]+)\"\\)\\s*=\\s*\"(?<name>[^\"]*)\"\\s*,\\s*\"(?<path>[^\"]*)\"\\s*,\\s*\"(?<id>[^\"]+)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private const string FolderType = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
    private const string ModernFolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

    public static SolutionDescription Inspect(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.EntryPath == null) return new(null, "folder", [], [], [], [StructuralNotice]);
        return InspectEntry(snapshot, WorkspacePath.Normalize(snapshot.EntryPath), new HashSet<string>(StringComparer.Ordinal));
    }

    private static SolutionDescription InspectEntry(WorkspaceSnapshot snapshot, string entry, HashSet<string> visited)
    {
        if (!visited.Add(entry) || visited.Count > 8) throw new ArgumentException("Cyclic or excessively nested solution filters.");
        var text = ReadText(snapshot, entry);
        var projects = ImmutableArray.CreateBuilder<SolutionProject>();
        var folders = ImmutableArray.CreateBuilder<string>();
        var items = ImmutableArray.CreateBuilder<string>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        diagnostics.Add(StructuralNotice);
        var format = Path.GetExtension(entry).ToLowerInvariant();
        if (WorkspacePath.IsProject(entry))
            AddProject(snapshot, entry, Path.GetFileNameWithoutExtension(entry), null, projects, diagnostics);
        else if (format == ".slnx")
        {
            var xml = ReadXml(text, "Solution");
            foreach (var folder in xml.Descendants().Where(node => node.Name.LocalName == "Folder"))
                folders.Add(FolderName(folder));
            foreach (var node in xml.Descendants().Where(node => node.Name.LocalName is "Project" or "File"))
            {
                if (node.Attribute("Path")?.Value is not { Length: > 0 } relative) continue;
                var path = Resolve(entry, relative, diagnostics);
                if (path == null) continue;
                if (node.Name.LocalName == "File") { items.Add(path); continue; }
                var folder = node.Ancestors().FirstOrDefault(parent => parent.Name.LocalName == "Folder");
                AddProject(snapshot, path, node.Attribute("DisplayName")?.Value ?? Path.GetFileNameWithoutExtension(path),
                    folder == null ? null : FolderName(folder), projects, diagnostics);
            }
        }
        else if (format == ".sln")
            ReadSln(snapshot, entry, text, projects, folders, items, diagnostics);
        else if (format == ".slnf")
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var filter = json.RootElement.GetProperty("solution");
            var target = WorkspacePath.Resolve(entry, filter.GetProperty("path").GetString() ?? throw new ArgumentException("Missing solution filter path."));
            var solution = InspectEntry(snapshot, target, visited);
            var selected = filter.GetProperty("projects").EnumerateArray()
                .Select(value => WorkspacePath.Resolve(target, value.GetString() ?? throw new ArgumentException("Invalid filter project.")))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var missing in selected.Where(path => !solution.Projects.Any(project => project.Path == path)))
                diagnostics.Add("Filter project is not present in the solution: " + missing);
            diagnostics.Add("Solution filter selection is structural; the SDK determines its evaluated dependency closure.");
            return solution with
            {
                EntryPath = entry, Format = "slnf",
                Projects = solution.Projects.Select(project => project with { Loaded = project.Loaded && selected.Contains(project.Path) }).ToImmutableArray(),
                Diagnostics = solution.Diagnostics.AddRange(diagnostics.Skip(1))
            };
        }
        else throw new ArgumentException("Unsupported solution/project format: " + format);
        return new(entry, format.TrimStart('.'), projects.ToImmutable(), folders.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            items.Distinct(StringComparer.Ordinal).ToImmutableArray(), diagnostics.ToImmutable());
    }

    public static ProjectDescription InspectProject(WorkspaceSnapshot snapshot, string path)
    {
        path = WorkspacePath.Normalize(path);
        var root = ReadXml(ReadText(snapshot, path), "Project").Root!;
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in root.Elements().Where(node => node.Name.LocalName == "PropertyGroup" && node.Attribute("Condition") == null))
            foreach (var property in group.Elements().Where(node => node.Attribute("Condition") == null))
                properties[property.Name.LocalName] = property.Value;
        var framework = properties.GetValueOrDefault("TargetFrameworks") ?? properties.GetValueOrDefault("TargetFramework") ?? "";
        var references = ImmutableArray.CreateBuilder<string>();
        var items = ImmutableArray.CreateBuilder<ProjectItemDescription>();
        foreach (var group in root.Elements().Where(node => node.Name.LocalName == "ItemGroup"))
        {
            foreach (var item in group.Elements())
            {
                var condition = string.Join(" And ", new[] { group.Attribute("Condition")?.Value, item.Attribute("Condition")?.Value }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => "(" + value + ")"));
                var include = item.Attribute("Include")?.Value ?? "";
                items.Add(new(item.Name.LocalName, include, item.Attribute("Remove")?.Value, item.Attribute("Update")?.Value,
                    item.Attribute("Exclude")?.Value, Child(item, "Link"), condition.Length == 0 ? null : condition,
                    item.Attribute("VersionOverride")?.Value ?? item.Attribute("Version")?.Value ?? Child(item, "VersionOverride") ?? Child(item, "Version")));
                if (item.Name.LocalName == "ProjectReference")
                    foreach (var reference in include.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (reference.IndexOfAny(['$', '@', '*', '?']) >= 0) { diagnostics.Add("Unevaluated project reference: " + reference); continue; }
                        if (Resolve(path, reference, diagnostics) is { } resolved) references.Add(resolved);
                    }
            }
        }
        if (root.Descendants().Any(node => node.Attribute("Condition") != null || node.Name.LocalName is "Import" or "Choose" or "Target") ||
            properties.Values.Any(value => value.Contains("$(", StringComparison.Ordinal))) diagnostics.Add(StructuralNotice);
        var sdk = root.Attribute("Sdk")?.Value ?? string.Join(';', root.Elements().Where(node => node.Name.LocalName == "Sdk")
            .Select(node => node.Attribute("Name")?.Value ?? ""));
        var language = Path.GetExtension(path).ToLowerInvariant() switch { ".csproj" => "C#", ".vbproj" => "Visual Basic", ".fsproj" => "F#", _ => "Unknown" };
        return new(path, Path.GetFileNameWithoutExtension(path), language, sdk, properties.ToImmutable(),
            framework.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray(),
            references.Distinct(StringComparer.Ordinal).ToImmutableArray(), items.ToImmutable(), diagnostics.ToImmutable());
    }

    public static string ReadText(WorkspaceSnapshot snapshot, string path)
    {
        if (!snapshot.Files.TryGetValue(path, out var file)) throw new FileNotFoundException("Workspace file not found.", path);
        if (file.IsBinary) throw new ArgumentException("Expected a text file: " + path);
        return file.Content;
    }

    public static XDocument ReadXml(string text, string rootName)
    {
        if (text.Length > VirtualWorkspace.MaximumFileCharacters) throw new ArgumentException("XML document limit exceeded.");
        using var source = new StringReader(text);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = VirtualWorkspace.MaximumFileCharacters, MaxCharactersFromEntities = 0
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName != rootName) throw new ArgumentException("Expected a " + rootName + " XML document.");
        foreach (var node in document.Descendants())
            if (node.Ancestors().Take(65).Count() > 64) throw new ArgumentException("XML nesting limit exceeded.");
        return document;
    }

    private static string? Child(XElement node, string name) => node.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;
    private static string FolderName(XElement folder)
    {
        var name = folder.Attribute("Name")?.Value ?? "";
        if (name.StartsWith('/')) return name.Trim('/');
        var ancestors = folder.Ancestors().Where(node => node.Name.LocalName == "Folder").Reverse()
            .Select(node => (node.Attribute("Name")?.Value ?? "").Trim('/'));
        return string.Join('/', ancestors.Append(name.Trim('/')).Where(part => part.Length != 0));
    }

    private static string? Resolve(string owner, string relative, ImmutableArray<string>.Builder diagnostics)
    {
        try { return WorkspacePath.Resolve(owner, relative); }
        catch (ArgumentException) { diagnostics.Add("Reference cannot be resolved inside the portable workspace: " + relative); return null; }
    }

    private static void AddProject(WorkspaceSnapshot snapshot, string path, string name, string? folder,
        ImmutableArray<SolutionProject>.Builder projects, ImmutableArray<string>.Builder diagnostics)
    {
        if (projects.Any(project => project.Path == path)) { diagnostics.Add("Duplicate project path: " + path); return; }
        ProjectDescription? definition = null;
        if (snapshot.Files.ContainsKey(path) && WorkspacePath.IsProject(path))
        {
            try { definition = InspectProject(snapshot, path); }
            catch (Exception error) when (error is ArgumentException or XmlException) { diagnostics.Add(path + ": " + error.Message); }
        }
        if (definition == null) diagnostics.Add("Project is missing or unsupported: " + path);
        projects.Add(new(path, name, folder, definition != null, definition));
    }

    private static void ReadSln(WorkspaceSnapshot snapshot, string entry, string text,
        ImmutableArray<SolutionProject>.Builder projects, ImmutableArray<string>.Builder folders,
        ImmutableArray<string>.Builder items, ImmutableArray<string>.Builder diagnostics)
    {
        var entries = new Dictionary<string, (string Name, string Path, bool Folder)>(StringComparer.OrdinalIgnoreCase);
        var nesting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inNesting = false; var inItems = false; var currentIsFolder = false;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("Project(", StringComparison.Ordinal))
            {
                var match = SlnProject.Match(line);
                if (!match.Success) { diagnostics.Add("Malformed solution project declaration."); continue; }
                var id = match.Groups["id"].Value;
                var type = match.Groups["type"].Value;
                currentIsFolder = type.Equals(FolderType, StringComparison.OrdinalIgnoreCase) || type.Equals(ModernFolderType, StringComparison.OrdinalIgnoreCase);
                if (!entries.TryAdd(id, (match.Groups["name"].Value, match.Groups["path"].Value, currentIsFolder)))
                    diagnostics.Add("Duplicate solution project identity: " + id);
                continue;
            }
            if (line.StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal)) { inNesting = true; continue; }
            if (line == "EndGlobalSection") { inNesting = false; continue; }
            if (line.StartsWith("ProjectSection(SolutionItems)", StringComparison.Ordinal)) { inItems = currentIsFolder; continue; }
            if (line is "EndProjectSection" or "EndProject") { inItems = false; continue; }
            var equal = line.IndexOf('=');
            if (equal < 0) continue;
            if (inNesting) nesting[line[..equal].Trim()] = line[(equal + 1)..].Trim();
            if (inItems && Resolve(entry, line[..equal].Trim(), diagnostics) is { } itemPath) items.Add(itemPath);
        }
        string? ParentFolder(string id)
        {
            var parts = new List<string>(); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (nesting.TryGetValue(id, out var parent))
            {
                if (!visited.Add(parent) || parts.Count > 64) { diagnostics.Add("Cyclic solution folders."); break; }
                if (!entries.TryGetValue(parent, out var folder) || !folder.Folder) break;
                parts.Add(folder.Name); id = parent;
            }
            parts.Reverse(); return parts.Count == 0 ? null : string.Join('/', parts);
        }
        foreach (var (id, project) in entries)
        {
            var parent = ParentFolder(id);
            if (project.Folder) folders.Add(parent == null ? project.Name : parent + "/" + project.Name);
            else if (Resolve(entry, project.Path, diagnostics) is { } path) AddProject(snapshot, path, project.Name, parent, projects, diagnostics);
        }
    }
}
