using XamlG.ProjectSystem;

namespace XamlG.Playground.Workspaces;

public sealed class SolutionExplorerNode(string id, string label, string kind, string? path = null, string? projectPath = null)
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Kind { get; } = kind;
    public string? Path { get; } = path;
    public string? ProjectPath { get; } = projectPath;
    public bool Missing { get; set; }
    public List<SolutionExplorerNode> Children { get; } = [];
    internal Dictionary<string, SolutionExplorerNode> Folders { get; } = new(StringComparer.Ordinal);
}
public sealed record SolutionExplorerRow(SolutionExplorerNode Node, int Depth);

public static class SolutionExplorerTree
{
    public static SolutionExplorerNode Build(SolutionWorkspaceSession session, bool folderView)
    {
        var root = new SolutionExplorerNode("solution-root", Path.GetFileName(session.Snapshot.EntryPath ?? "Workspace"), "solution", session.Snapshot.EntryPath);
        var files = session.Inventory.Where(file => !file.IsDirectory).ToArray();
        if (folderView || session.Snapshot.EntryPath == null)
        {
            foreach (var file in files) AddFile(root, file.Path, file.Path, null);
            Sort(root); return root;
        }
        foreach (var folder in session.Description.Folders) Folder(root, folder, "solution-folder");
        var projectDirectories = new Dictionary<string, List<SolutionExplorerNode>>(StringComparer.Ordinal);
        var projectPaths = new HashSet<string>(StringComparer.Ordinal);
        var present = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var project in session.Description.Projects)
        {
            var parent = project.Folder == null ? root : Folder(root, project.Folder, "solution-folder");
            var node = new SolutionExplorerNode("project:" + project.Path, project.Name, "project", project.Path, project.Path) { Missing = !project.Loaded };
            parent.Children.Add(node); projectPaths.Add(project.Path);
            var directory = WorkspacePath.Directory(project.Path);
            if (!projectDirectories.TryGetValue(directory, out var owners)) projectDirectories[directory] = owners = [];
            owners.Add(node);
            if (project.Definition is not { } definition) continue;
            var dependencies = new SolutionExplorerNode(node.Id + ":dependencies", "Dependencies (authored)", "dependencies", projectPath: project.Path);
            if (definition.Sdk.Length != 0) dependencies.Children.Add(new(node.Id + ":sdk", definition.Sdk, "sdk", projectPath: project.Path));
            foreach (var reference in definition.ProjectReferences)
                dependencies.Children.Add(new(node.Id + ":ref:" + reference, Path.GetFileNameWithoutExtension(reference), "project-reference", reference, project.Path) { Missing = !present.Contains(reference) });
            foreach (var item in definition.Items.Where(item => item.Kind is "PackageReference" or "FrameworkReference" or "Reference"))
                dependencies.Children.Add(new(node.Id + ":" + item.Kind + ":" + dependencies.Children.Count, item.Include + (item.Version == null ? "" : " · " + item.Version), "package", projectPath: project.Path));
            if (dependencies.Children.Count != 0) node.Children.Add(dependencies);
            foreach (var item in definition.Items.Where(item => item.Link != null && item.Include.Length != 0 && item.Include.IndexOfAny(['$', '@', '*', '?', ';']) < 0))
            {
                try
                {
                    var path = WorkspacePath.Resolve(project.Path, item.Include);
                    var linked = AddFile(node, item.Link!, path, project.Path);
                    linked.Missing = !present.Contains(path);
                }
                catch (ArgumentException) { }
            }
        }
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (projectPaths.Contains(file.Path) || file.Path == session.Snapshot.EntryPath) { assigned.Add(file.Path); continue; }
            var directory = WorkspacePath.Directory(file.Path);
            while (true)
            {
                if (projectDirectories.TryGetValue(directory, out var owners))
                {
                    foreach (var owner in owners)
                    {
                        var relative = directory.Length == 0 ? file.Path : file.Path[(directory.Length + 1)..];
                        AddFile(owner, relative, file.Path, owner.ProjectPath);
                    }
                    assigned.Add(file.Path); break;
                }
                if (directory.Length == 0) break;
                directory = WorkspacePath.Directory(directory);
            }
        }
        foreach (var item in session.Description.SolutionItems)
        {
            var node = AddFile(Folder(root, "Solution Items", "solution-folder"), item, item, null);
            node.Missing = !present.Contains(item); assigned.Add(item);
        }
        if (files.Any(file => !assigned.Contains(file.Path)))
        {
            var remainder = Folder(root, "Workspace files", "folder");
            foreach (var file in files.Where(file => !assigned.Contains(file.Path))) AddFile(remainder, file.Path, file.Path, null);
        }
        Sort(root); return root;
    }

    public static List<SolutionExplorerRow> Flatten(SolutionExplorerNode root, ISet<string> expanded, string filter)
    {
        var result = new List<SolutionExplorerRow>();
        var searching = !string.IsNullOrWhiteSpace(filter);
        var matches = new HashSet<string>(StringComparer.Ordinal);
        if (searching)
        {
            bool Mark(SolutionExplorerNode node)
            {
                var match = node.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || node.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
                foreach (var child in node.Children) match |= Mark(child);
                if (match) matches.Add(node.Id);
                return match;
            }
            Mark(root);
        }
        void Visit(SolutionExplorerNode node, int depth)
        {
            if (searching && !matches.Contains(node.Id)) return;
            result.Add(new(node, depth));
            if (searching || expanded.Contains(node.Id)) foreach (var child in node.Children) Visit(child, depth + 1);
        }
        Visit(root, 0); return result;
    }

    private static SolutionExplorerNode Folder(SolutionExplorerNode parent, string path, string kind)
    {
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!parent.Folders.TryGetValue(segment, out var folder))
            {
                folder = new(parent.Id + "/" + segment, segment, kind, projectPath: parent.ProjectPath);
                parent.Folders.Add(segment, folder); parent.Children.Add(folder);
            }
            parent = folder;
        }
        return parent;
    }
    private static SolutionExplorerNode AddFile(SolutionExplorerNode parent, string displayPath, string actualPath, string? projectPath)
    {
        displayPath = displayPath.Replace('\\', '/');
        var slash = displayPath.LastIndexOf('/');
        if (slash >= 0) parent = Folder(parent, displayPath[..slash], "folder");
        var id = parent.Id + ":file:" + actualPath;
        var existing = parent.Children.FirstOrDefault(child => child.Id == id);
        if (existing != null) return existing;
        var file = new SolutionExplorerNode(id, Path.GetFileName(displayPath), "file", actualPath, projectPath);
        parent.Children.Add(file); return file;
    }
    private static void Sort(SolutionExplorerNode node)
    {
        node.Children.Sort((left, right) =>
        {
            var group = (left.Children.Count == 0 ? 1 : 0).CompareTo(right.Children.Count == 0 ? 1 : 0);
            return group != 0 ? group : StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label);
        });
        foreach (var child in node.Children) Sort(child);
    }
}
