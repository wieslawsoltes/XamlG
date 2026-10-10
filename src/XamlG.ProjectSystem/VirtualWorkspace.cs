using System.Collections.Immutable;

namespace XamlG.ProjectSystem;

public sealed record WorkspaceFile(string Content, bool IsBinary = false);
public sealed record WorkspaceChange(string Path, WorkspaceFile? Expected, WorkspaceFile? Replacement);
public sealed record WorkspaceSnapshot(long Revision, string? EntryPath, string? StartupProject,
    ImmutableDictionary<string, WorkspaceFile> Files);

/// <summary>Portable paths are ordinal, relative and safe to materialize on Windows, macOS or Linux.</summary>
public static class WorkspacePath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 1024 || path[0] is '/' or '\\' || path.Contains(':') || path.Any(char.IsControl))
            throw new ArgumentException("A workspace path must be a bounded relative path.", nameof(path));
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new ArgumentException("The path escapes the workspace.", nameof(path));
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            ValidateName(part);
            parts.Add(part);
        }
        if (parts.Count == 0) throw new ArgumentException("A file path is required.", nameof(path));
        return string.Join('/', parts);
    }

    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Use a portable file or directory name.", nameof(name));
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            throw new ArgumentException("Reserved device names are not portable workspace names.", nameof(name));
    }

    public static string Directory(string path)
    {
        path = Normalize(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string Resolve(string ownerFile, string relativePath) =>
        Normalize((Directory(ownerFile) is { Length: > 0 } directory ? directory + "/" : "") + RejectRooted(relativePath));

    private static string RejectRooted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] is '/' or '\\' || path.Contains(':')) throw new ArgumentException("An absolute reference is outside the portable workspace.");
        return path;
    }

    public static string RelativeTo(string ownerFile, string target)
    {
        var from = Directory(ownerFile).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = Normalize(target).Split('/');
        var shared = 0;
        while (shared < from.Length && shared < to.Length && from[shared] == to[shared]) shared++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - shared).Concat(to.Skip(shared)));
    }

    public static bool IsProject(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".vbproj" or ".fsproj";
    public static bool IsEntry(string path) => IsProject(path) || Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf";
}

/// <summary>
/// Immutable publication with revision and complete-before-image checks. A failed transaction never
/// changes the current snapshot. Browser storage and local file adapters are intentionally separate.
/// </summary>
public sealed class VirtualWorkspace
{
    public const int MaximumFiles = 20_000;
    public const int MaximumFileCharacters = 4 * 1024 * 1024;
    public const int MaximumTotalCharacters = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private WorkspaceSnapshot _current;
    public WorkspaceSnapshot Current { get { lock (_gate) return _current; } }

    public VirtualWorkspace(IEnumerable<KeyValuePair<string, WorkspaceFile>>? files = null, string? entryPath = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, WorkspaceFile>(StringComparer.Ordinal);
        if (files != null)
            foreach (var (path, file) in files)
            {
                var normalized = WorkspacePath.Normalize(path);
                if (!builder.TryAdd(normalized, file)) throw new ArgumentException("Duplicate normalized path: " + normalized);
                if (builder.Count > MaximumFiles) throw new ArgumentException("Workspace file limit exceeded.");
            }
        var snapshot = builder.ToImmutable();
        ValidateFiles(snapshot);
        entryPath = ValidateEntry(snapshot, entryPath);
        _current = new(0, entryPath, null, snapshot);
    }

    public WorkspaceSnapshot Apply(long expectedRevision, IEnumerable<WorkspaceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            var next = _current.Files.ToBuilder();
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                var path = WorkspacePath.Normalize(change.Path);
                if (!touched.Add(path)) throw new ArgumentException("A transaction may change a path only once: " + path);
                if (touched.Count > MaximumFiles) throw new ArgumentException("Transaction file limit exceeded.");
                if (_current.Files.GetValueOrDefault(path) != change.Expected)
                    throw new InvalidOperationException("The file changed since this edit was prepared: " + path);
                if (change.Replacement == null) next.Remove(path);
                else next[path] = change.Replacement;
            }
            var files = next.ToImmutable();
            ValidateFiles(files);
            if (files.Count == _current.Files.Count && files.All(pair => _current.Files.GetValueOrDefault(pair.Key) == pair.Value)) return _current;
            _current = _current with
            {
                Revision = checked(_current.Revision + 1), Files = files,
                EntryPath = _current.EntryPath is { } entry && files.ContainsKey(entry) ? entry : null,
                StartupProject = _current.StartupProject is { } startup && files.ContainsKey(startup) ? startup : null
            };
            return _current;
        }
    }

    public WorkspaceSnapshot Open(long expectedRevision, string entryPath)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            var entry = ValidateEntry(_current.Files, entryPath);
            if (_current.EntryPath == entry) return _current;
            _current = _current with { Revision = checked(_current.Revision + 1), EntryPath = entry, StartupProject = null };
            return _current;
        }
    }

    public WorkspaceSnapshot SetStartupProject(long expectedRevision, string path)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            path = WorkspacePath.Normalize(path);
            if (!SolutionInspector.Inspect(_current).Projects.Any(project => project.Path == path && project.Loaded))
                throw new ArgumentException("Choose a loaded project in the current solution.");
            if (_current.StartupProject == path) return _current;
            _current = _current with { Revision = checked(_current.Revision + 1), StartupProject = path };
            return _current;
        }
    }

    public WorkspaceSnapshot Move(long expectedRevision, string source, string destination)
    {
        lock (_gate)
        {
            CheckRevision(expectedRevision);
            source = WorkspacePath.Normalize(source); destination = WorkspacePath.Normalize(destination);
            if (!_current.Files.TryGetValue(source, out var file)) throw new FileNotFoundException("Source file not found.", source);
            if (source == destination) return _current;
            var entry = _current.EntryPath == source;
            var startup = _current.StartupProject == source;
            var result = Apply(expectedRevision, [new(source, file, null), new(destination, null, file)]);
            // Moving a project/solution does not silently rewrite unknown MSBuild imports or references.
            _current = result with { EntryPath = entry ? destination : result.EntryPath, StartupProject = startup ? destination : result.StartupProject };
            return _current;
        }
    }

    private void CheckRevision(long expected)
    {
        if (_current.Revision != expected) throw new InvalidOperationException("Workspace revision conflict. Reload before applying this change.");
    }

    private static string? ValidateEntry(ImmutableDictionary<string, WorkspaceFile> files, string? entry)
    {
        if (entry == null) return null;
        entry = WorkspacePath.Normalize(entry);
        if (!WorkspacePath.IsEntry(entry) || !files.TryGetValue(entry, out var file) || file.IsBinary)
            throw new ArgumentException("Choose an existing solution or project file.", nameof(entry));
        return entry;
    }

    private static void ValidateFiles(ImmutableDictionary<string, WorkspaceFile> files)
    {
        if (files.Count > MaximumFiles) throw new ArgumentException("Workspace file limit exceeded.");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (path, file) in files)
        {
            if (file?.Content == null || file.Content.Length > MaximumFileCharacters)
                throw new ArgumentException("Workspace file content limit exceeded: " + path);
            total += file.Content.Length;
            if (total > MaximumTotalCharacters) throw new ArgumentException("Workspace total content limit exceeded.");
            if (!names.TryAdd(path, path)) throw new ArgumentException("Case-insensitive file collision: " + path);
            var segments = path.Split('/');
            var directory = "";
            for (var i = 0; i < segments.Length - 1; i++)
            {
                directory = directory.Length == 0 ? segments[i] : directory + "/" + segments[i];
                directories.Add(directory);
            }
            if (file.IsBinary)
            {
                try { _ = Convert.FromBase64String(file.Content); }
                catch (FormatException error) { throw new ArgumentException("Binary files require valid base64: " + path, error); }
            }
        }
        foreach (var directory in directories)
            if (names.ContainsKey(directory)) throw new ArgumentException("A path cannot be both a file and directory: " + directory);
        // Directory spelling must also agree across files for deterministic cross-platform exports.
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Keys)
        {
            var directory = WorkspacePath.Directory(path);
            while (directory.Length != 0)
            {
                if (spellings.TryGetValue(directory, out var spelling) && spelling != directory)
                    throw new ArgumentException("Case-insensitive directory collision: " + directory);
                spellings[directory] = directory;
                directory = WorkspacePath.Directory(directory);
            }
        }
    }
}
