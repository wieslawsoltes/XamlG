using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XamlG.ProjectSystem;

/// <summary>Edits authored XML without claiming to evaluate effective MSBuild values.</summary>
public static class ProjectFileEditor
{
    public static WorkspaceChange SetProperty(WorkspaceSnapshot snapshot, string path, string name, string value)
    {
        XmlConvert.VerifyNCName(name);
        ArgumentNullException.ThrowIfNull(value);
        return Edit(snapshot, path, "Project", document =>
        {
            var root = document.Root!;
            var group = root.Elements().LastOrDefault(node => node.Name.LocalName == "PropertyGroup" && node.Attribute("Condition") == null);
            if (group == null) { group = new XElement(root.Name.Namespace + "PropertyGroup"); root.Add(group); }
            var property = group.Elements().LastOrDefault(node => node.Name.LocalName == name && node.Attribute("Condition") == null);
            if (property == null) group.Add(new XElement(root.Name.Namespace + name, value));
            else property.Value = value;
        });
    }

    public static WorkspaceChange AddProjectReference(WorkspaceSnapshot snapshot, string path, string reference)
    {
        path = WorkspacePath.Normalize(path); reference = WorkspacePath.Normalize(reference);
        if (path == reference) throw new ArgumentException("A project cannot reference itself.");
        if (!WorkspacePath.IsProject(reference) || !snapshot.Files.ContainsKey(reference)) throw new ArgumentException("The referenced project must exist in the workspace.");
        return AddItem(snapshot, path, "ProjectReference", WorkspacePath.RelativeTo(path, reference), null);
    }

    public static WorkspaceChange AddPackageReference(WorkspaceSnapshot snapshot, string path, string id, string? version)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Invalid package identifier.");
        return AddItem(snapshot, path, "PackageReference", id, version);
    }

    public static WorkspaceChange AddProject(WorkspaceSnapshot snapshot, string solutionPath, string projectPath, string? folder = null)
    {
        solutionPath = WorkspacePath.Normalize(solutionPath); projectPath = WorkspacePath.Normalize(projectPath);
        RequireSlnx(solutionPath);
        if (!WorkspacePath.IsProject(projectPath)) throw new ArgumentException("A project path is required.");
        var relative = WorkspacePath.RelativeTo(solutionPath, projectPath);
        return Edit(snapshot, solutionPath, "Solution", document =>
        {
            foreach (var project in document.Descendants().Where(node => node.Name.LocalName == "Project"))
                if (project.Attribute("Path") is { } existing && WorkspacePath.Resolve(solutionPath, existing.Value) == projectPath)
                    throw new InvalidOperationException("The solution already contains this project.");
            var parent = document.Root!;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var name = "/" + WorkspacePath.Normalize(folder.Trim('/')) + "/";
                var existing = parent.Elements().FirstOrDefault(node => node.Name.LocalName == "Folder" && (string?)node.Attribute("Name") == name);
                if (existing == null) { existing = new XElement(parent.Name.Namespace + "Folder", new XAttribute("Name", name)); parent.Add(existing); }
                parent = existing;
            }
            parent.Add(new XElement(parent.Name.Namespace + "Project", new XAttribute("Path", relative)));
        });
    }

    public static WorkspaceChange RemoveProject(WorkspaceSnapshot snapshot, string solutionPath, string projectPath)
    {
        RequireSlnx(solutionPath);
        projectPath = WorkspacePath.Normalize(projectPath);
        return Edit(snapshot, solutionPath, "Solution", document =>
        {
            foreach (var project in document.Descendants().Where(node => node.Name.LocalName == "Project" && node.Attribute("Path") != null).ToArray())
                if (WorkspacePath.Resolve(solutionPath, project.Attribute("Path")!.Value) == projectPath) project.Remove();
        });
    }

    public static WorkspaceChange AddSolutionFolder(WorkspaceSnapshot snapshot, string path, string folder)
    {
        RequireSlnx(path);
        var name = "/" + WorkspacePath.Normalize(folder.Trim('/')) + "/";
        return Edit(snapshot, path, "Solution", document =>
        {
            if (document.Descendants().Any(node => node.Name.LocalName == "Folder" && (string?)node.Attribute("Name") == name)) return;
            document.Root!.Add(new XElement(document.Root.Name.Namespace + "Folder", new XAttribute("Name", name)));
        });
    }

    private static WorkspaceChange AddItem(WorkspaceSnapshot snapshot, string path, string kind, string include, string? version) =>
        Edit(snapshot, path, "Project", document =>
        {
            var root = document.Root!;
            var groups = root.Elements().Where(node => node.Name.LocalName == "ItemGroup" && node.Attribute("Condition") == null).ToArray();
            var existing = groups.SelectMany(group => group.Elements()).FirstOrDefault(item => item.Name.LocalName == kind &&
                string.Equals((string?)item.Attribute("Include"), include, kind == "PackageReference" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && item.Attribute("Condition") == null);
            if (existing != null) { if (version != null) existing.SetAttributeValue("Version", version); return; }
            var group = groups.LastOrDefault();
            if (group == null) { group = new XElement(root.Name.Namespace + "ItemGroup"); root.Add(group); }
            var item = new XElement(root.Name.Namespace + kind, new XAttribute("Include", include));
            if (version != null) item.SetAttributeValue("Version", version);
            group.Add(item);
        });

    private static WorkspaceChange Edit(WorkspaceSnapshot snapshot, string path, string rootName, Action<XDocument> edit)
    {
        path = WorkspacePath.Normalize(path);
        var original = snapshot.Files.GetValueOrDefault(path) ?? throw new FileNotFoundException("Workspace file not found.", path);
        var document = SolutionInspector.ReadXml(SolutionInspector.ReadText(snapshot, path), rootName);
        edit(document);
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = document.Declaration == null, Indent = false, NewLineHandling = NewLineHandling.None })) document.Save(writer);
        return new(path, original, new(output.ToString()));
    }

    private static void RequireSlnx(string path)
    {
        if (!Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Structured offline edits require .slnx. Use the trusted SDK for .sln changes, or edit the solution text.");
    }
}
