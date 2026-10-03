using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace XamlG.Workspaces.Watching;

/// <summary>Explicit files plus source roots. Build outputs are ignored unless they are actual compiler inputs.</summary>
public sealed class XamlWatchInputs
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ImmutableHashSet<string> SourceExtensions = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        ".cs", ".xaml", ".axaml", ".csproj", ".props", ".targets", ".sln", ".slnx", ".resx", ".editorconfig");
    public XamlWatchInputs(IEnumerable<string> files, IEnumerable<string>? sourceRoots = null)
    {
        Files = files.Select(Path.GetFullPath).ToImmutableHashSet(PathComparer);
        SourceRoots = (sourceRoots ?? Array.Empty<string>()).Select(Path.GetFullPath).Distinct(PathComparer).ToImmutableArray();
    }
    public ImmutableHashSet<string> Files { get; }
    public ImmutableArray<string> SourceRoots { get; }

    public bool Contains(string path)
    {
        path = Path.GetFullPath(path);
        if (Files.Contains(path)) return true;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in SourceRoots)
        {
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, comparison)) continue;
            var relative = path.Substring(prefix.Length);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git" or "node_modules")) return false;
            return SourceExtensions.Contains(Path.GetExtension(path)) || Path.GetFileName(path) is "global.json" or "NuGet.Config" or "nuget.config" or "packages.lock.json";
        }
        return false;
    }

    public static XamlWatchInputs FromProject(Project project)
    {
        var files = new HashSet<string>(PathComparer);
        var roots = new HashSet<string>(PathComparer);
        var visited = new HashSet<ProjectId>();
        var pending = new Stack<Project>(); pending.Push(project);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current.Id)) continue;
            foreach (var document in current.Documents.Concat(current.AdditionalDocuments).Concat(current.AnalyzerConfigDocuments))
                if (document.FilePath is { } path) files.Add(path);
            foreach (var reference in current.AnalyzerReferences)
                if (reference.FullPath is { } path) files.Add(path);
            foreach (var reference in current.MetadataReferences.OfType<PortableExecutableReference>())
                if (reference.FilePath is { } path) files.Add(path);
            if (current.FilePath is { } projectPath)
            {
                files.Add(projectPath);
                var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
                roots.Add(directory);
                files.Add(Path.Combine(directory, "obj", "project.assets.json"));
                for (var ancestor = new DirectoryInfo(directory); ancestor != null; ancestor = ancestor.Parent)
                    foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config" })
                        files.Add(Path.Combine(ancestor.FullName, name));
            }
            foreach (var reference in current.ProjectReferences)
                if (current.Solution.GetProject(reference.ProjectId) is { } referenced) pending.Push(referenced);
        }
        return new(files, roots);
    }
}
