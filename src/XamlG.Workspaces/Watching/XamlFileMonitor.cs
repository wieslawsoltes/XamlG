namespace XamlG.Workspaces.Watching;

/// <summary>Watches atomic saves, creations, deletions and renames; an overflow requests a full refresh.</summary>
public sealed class XamlFileMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _changed;
    private readonly Action<Exception>? _report;
    private List<FileSystemWatcher> _watchers = new();
    private XamlWatchInputs _inputs;
    private bool _disposed;

    public XamlFileMonitor(XamlWatchInputs inputs, Action changed, Action<Exception>? report = null)
    {
        _inputs = inputs;
        _changed = changed;
        _report = report;
        Update(inputs);
    }

    public void Update(XamlWatchInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var roots = inputs.SourceRoots.Where(Directory.Exists).Distinct(XamlWatchInputs.PathComparer).ToArray();
            var directories = new Dictionary<string, bool>(XamlWatchInputs.PathComparer);
            foreach (var root in roots) directories[root] = true;
            foreach (var file in inputs.Files)
            {
                var directory = Path.GetDirectoryName(file);
                while (directory != null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
                if (directory != null && !directories.ContainsKey(directory)) directories.Add(directory, false);
            }
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var directory in directories.Keys.ToArray())
                if (roots.Any(root => !XamlWatchInputs.PathComparer.Equals(root, directory) && directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison)))
                    directories.Remove(directory);
            if (directories.Count > 256) throw new InvalidOperationException("The project requires more than 256 watch directories; supply a narrower explicit watch set.");
            var replacements = new List<FileSystemWatcher>();
            try
            {
                foreach (var directory in directories)
                {
                    var watcher = new FileSystemWatcher(directory.Key)
                    {
                        IncludeSubdirectories = directory.Value,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 32768
                    };
                    watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                    watcher.Renamed += Renamed; watcher.Error += Error;
                    replacements.Add(watcher);
                }
                Volatile.Write(ref _inputs, inputs);
                foreach (var watcher in replacements) watcher.EnableRaisingEvents = true;
            }
            catch { foreach (var watcher in replacements) watcher.Dispose(); throw; }
            var previous = _watchers;
            _watchers = replacements;
            foreach (var watcher in previous) watcher.Dispose();
        }
    }

    private void Changed(object sender, FileSystemEventArgs args) => Notify(args.FullPath);
    private void Renamed(object sender, RenamedEventArgs args) { Notify(args.OldFullPath); Notify(args.FullPath); }
    private void Error(object sender, ErrorEventArgs args)
    {
        try { _report?.Invoke(args.GetException()); _changed(); } catch { }
    }
    private void Notify(string path)
    {
        try { if (!_disposed && Volatile.Read(ref _inputs).Contains(path)) _changed(); }
        catch (Exception error) { try { _report?.Invoke(error); } catch { } }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
