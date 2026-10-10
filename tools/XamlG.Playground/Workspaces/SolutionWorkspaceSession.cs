using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.JSInterop;
using XamlG.Playground.Components;
using XamlG.ProjectSystem;

namespace XamlG.Playground.Workspaces;

public sealed record ImportedWorkspaceFile(string Path, string Content, bool IsBinary = false);
public sealed record WorkspaceInventoryFile(string Path, bool IsDirectory, long Length);
public sealed record NativeWorkspaceStatus(bool Enabled, string? Root, string Backend, bool TrustRequired, int MaximumFileBytes, string Message);
public sealed record NativeWorkspaceFile(string Path, string Hash, WorkspaceFile File, string Encoding);
public sealed record SavedSolutionWorkspace(int Format, long Revision, string Identity, string? EntryPath, string? StartupProject,
    ImportedWorkspaceFile[] Files, string[] OpenDocuments, string? SelectedProject = null);

public sealed class WorkspaceEditorDocument(string id, string path, WorkspaceFile file, string? diskHash = null, string encoding = "utf-8")
{
    public string Id { get; } = id;
    public string Path { get; } = path;
    public WorkspaceFile SavedFile { get; set; } = file;
    public string Text { get; set; } = file.Content;
    public bool IsBinary => SavedFile.IsBinary;
    public string? DiskHash { get; set; } = diskHash;
    public string Encoding { get; set; } = encoding;
    public bool Dirty => !IsBinary && Text != SavedFile.Content;
    public string? Error { get; set; }
    public CodeEditor? Editor;
}

/// <summary>
/// One active solution, independent from the legacy single-view preview compilation. Browser files
/// are revisioned in IndexedDB; local buffers use disk hashes and are not persisted in browser storage.
/// </summary>
public sealed class SolutionWorkspaceSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private IJSObjectReference? _module;
    private Task? _initialization;
    private long _storageRevision;
    private VirtualWorkspace _workspace = new();
    private string _identity = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, NativeWorkspaceFile> _nativeFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceEditorDocument> _documents = new(StringComparer.Ordinal);
    private WorkspaceInventoryFile[] _inventory = [];
    private bool _disposed;
    public event Action? Changed;
    public WorkspaceSnapshot Snapshot => _workspace.Current;
    public IReadOnlyDictionary<string, WorkspaceEditorDocument> Documents => _documents;
    public IReadOnlyList<WorkspaceInventoryFile> Inventory => IsLocal ? _inventory : Snapshot.Files.Select(pair => new WorkspaceInventoryFile(pair.Key, false, pair.Value.Content.Length)).ToArray();
    public SolutionDescription Description { get; private set; } = SolutionInspector.Inspect(new VirtualWorkspace().Current);
    public bool IsLocal { get; private set; }
    public bool Ready { get; private set; }
    public bool Busy { get; private set; }
    public bool Trusted { get; set; }
    public NativeWorkspaceStatus? LocalStatus { get; private set; }
    public string? SelectedProject { get; set; }
    public string Configuration { get; set; } = "Debug";
    public string Platform { get; set; } = "AnyCPU";
    public string? Framework { get; set; }
    public string Output { get; private set; } = "Open a solution or create a project. Browser inspection does not execute MSBuild.";
    public string? Error { get; private set; }
    public JsonElement? EvaluatedGraph { get; private set; }
    public JsonElement? TemplateCatalog { get; private set; }
    public bool HasUnsavedChanges => _documents.Values.Any(document => document.Dirty);
    public IEnumerable<string> EntryPaths => Inventory.Where(file => !file.IsDirectory && WorkspacePath.IsEntry(file.Path)).Select(file => file.Path).Order(StringComparer.Ordinal);
    public string DocumentKey(string path) => "workspace/" + _identity + "/" + path;

    public Task InitializeAsync(IJSRuntime javascript) => _initialization ??= InitializeCoreAsync(javascript);
    private async Task InitializeCoreAsync(IJSRuntime javascript)
    {
        try
        {
            _module = await javascript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "solution-workspace.js");
            var saved = await _module.InvokeAsync<SavedSolutionWorkspace?>("loadWorkspace");
            if (saved != null)
            {
                if (saved.Format != 1) throw new InvalidOperationException("This saved workspace uses an unsupported format. Export or reset it before continuing.");
                _storageRevision = saved.Revision;
                _workspace = new(saved.Files.Select(file => KeyValuePair.Create(file.Path, new WorkspaceFile(file.Content, file.IsBinary))), saved.EntryPath);
                _identity = Guid.NewGuid().ToString("N");
                SelectedProject = saved.SelectedProject;
                Inspect();
                if (saved.StartupProject != null && Description.Projects.Any(project => project.Path == saved.StartupProject && project.Loaded))
                    _workspace.SetStartupProject(Snapshot.Revision, saved.StartupProject);
                foreach (var path in saved.OpenDocuments.Take(64))
                    if (Snapshot.Files.TryGetValue(path, out var file)) AddDocument(path, file);
            }
            Ready = true;
        }
        catch (Exception error) { Error = error.Message; Ready = _module != null; }
        Notify();
    }

    public async Task CreateBrowserAsync(string name, string format, ProjectTemplateRequest? project, bool createSolution, bool addToCurrent)
    {
        await ExecuteAsync(async () =>
        {
            if (IsLocal) throw new InvalidOperationException("Switch to a browser workspace before creating offline projects.");
            if (addToCurrent)
            {
                if (project == null || Snapshot.EntryPath == null) throw new ArgumentException("Choose a project and an open solution.");
                var plan = WorkspaceTemplates.CreateProject(project);
                var membership = await SolutionFileService.AddProjectAsync(Snapshot, Snapshot.EntryPath, plan.EntryPath);
                await ApplyBrowserAsync(plan.Changes.Append(membership).ToArray());
                SelectedProject = plan.EntryPath;
            }
            else
            {
                var plan = createSolution ? await SolutionFileService.CreateAsync(name, format, project)
                    : WorkspaceTemplates.CreateProject(project ?? throw new ArgumentException("Choose a project template."));
                await ReplaceBrowserAsync(plan.CreateWorkspace(), []);
                SelectedProject = project == null ? null : Description.Projects.FirstOrDefault()?.Path;
            }
            Output = "Created an offline starter. Connect the local companion for installed templates, restore, MSBuild evaluation and builds.";
        });
    }

    public async Task ImportAsync(ImportedWorkspaceFile[] files, string? entryPath = null)
    {
        await ExecuteAsync(async () =>
        {
            var candidate = new VirtualWorkspace(files.Select(file => KeyValuePair.Create(file.Path, new WorkspaceFile(file.Content, file.IsBinary))));
            var entries = candidate.Current.Files.Keys.Where(WorkspacePath.IsEntry).Order(StringComparer.Ordinal).ToArray();
            entryPath ??= entries.FirstOrDefault(path => path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                ?? entries.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault();
            if (entryPath != null) candidate.Open(candidate.Current.Revision, entryPath);
            await ReplaceBrowserAsync(candidate, []);
            Output = $"Imported {files.Length:N0} files. Select a solution/project to inspect its structure.";
        });
    }

    public async Task OpenEntryAsync(string path)
    {
        await ExecuteAsync(async () =>
        {
            if (IsLocal) await LoadNativeProjectFilesAsync();
            _workspace.Open(Snapshot.Revision, path);
            Inspect(); SelectedProject = Description.Projects.FirstOrDefault(project => project.Loaded)?.Path;
            Framework = null; EvaluatedGraph = null;
            if (!IsLocal) await PersistBrowserAsync(Snapshot.Files, path);
        });
    }

    public async Task ConnectLocalAsync()
    {
        await ExecuteAsync(async () =>
        {
            LocalStatus = await NativeAsync<NativeWorkspaceStatus>("workspace_status", new { });
            if (!LocalStatus.Enabled) throw new InvalidOperationException(LocalStatus.Message);
            var inventory = await NativeAsync<WorkspaceInventoryFile[]>("workspace_list", new { });
            IsLocal = true; Trusted = false; _identity = Guid.NewGuid().ToString("N");
            _documents.Clear(); _nativeFiles.Clear(); _workspace = new(); _inventory = inventory;
            SelectedProject = null; EvaluatedGraph = null; Inspect();
            Output = "Local workspace connected. Choose a solution/project. Reading files does not authorize SDK execution.";
        });
    }

    public async Task RefreshLocalAsync()
    {
        await ExecuteAsync(async () =>
        {
            RequireLocal();
            _inventory = await NativeAsync<WorkspaceInventoryFile[]>("workspace_list", new { });
            await LoadNativeProjectFilesAsync();
            Inspect(); EvaluatedGraph = null;
        });
    }

    private async Task LoadNativeProjectFilesAsync()
    {
        var updates = new List<WorkspaceChange>();
        foreach (var path in _inventory.Where(file => !file.IsDirectory && WorkspacePath.IsEntry(file.Path)).Select(file => file.Path))
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path });
            if (_documents.Values.FirstOrDefault(document => document.Path == path) is { } open && open.Dirty)
            {
                if (open.DiskHash != disk.Hash) open.Error = "The file changed on disk while this buffer has unsaved edits. Save will require resolving the conflict.";
                continue;
            }
            updates.Add(new(path, Snapshot.Files.GetValueOrDefault(path), disk.File));
            _nativeFiles[path] = disk;
            if (_documents.Values.FirstOrDefault(document => document.Path == path) is { } clean)
            { clean.Text = disk.File.Content; clean.SavedFile = disk.File; clean.DiskHash = disk.Hash; clean.Encoding = disk.Encoding; }
        }
        if (updates.Count != 0) _workspace.Apply(Snapshot.Revision, updates);
    }

    public async Task<WorkspaceEditorDocument?> OpenDocumentAsync(string path)
    {
        WorkspaceEditorDocument? opened = null;
        await ExecuteAsync(async () =>
        {
            path = WorkspacePath.Normalize(path);
            opened = _documents.Values.FirstOrDefault(document => document.Path == path);
            if (opened != null) return;
            if (IsLocal)
            {
                var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path });
                _nativeFiles[path] = disk;
                _workspace.Apply(Snapshot.Revision, [new(path, Snapshot.Files.GetValueOrDefault(path), disk.File)]);
                opened = AddDocument(path, disk.File, disk.Hash, disk.Encoding);
            }
            else opened = AddDocument(path, Snapshot.Files.GetValueOrDefault(path) ?? throw new FileNotFoundException("The file is unavailable.", path));
            if (WorkspacePath.IsProject(path)) SelectedProject = path;
        });
        return opened;
    }

    private WorkspaceEditorDocument AddDocument(string path, WorkspaceFile file, string? hash = null, string encoding = "utf-8")
    {
        var id = "workspace:" + _identity + ":" + Uri.EscapeDataString(path);
        var document = new WorkspaceEditorDocument(id, path, file, hash, encoding);
        _documents.Add(id, document); return document;
    }

    public void Edit(WorkspaceEditorDocument document, string text)
    {
        if (_disposed || !_documents.TryGetValue(document.Id, out var current) || !ReferenceEquals(current, document) || document.IsBinary) return;
        if (text.Length > VirtualWorkspace.MaximumFileCharacters) { document.Error = "File content limit exceeded."; Notify(); return; }
        document.Text = text; document.Error = null; Notify();
    }

    public async Task CaptureAsync(WorkspaceEditorDocument document)
    {
        if (document.Editor is { IsRetired: false } editor && await editor.TryGetTextAsync() is { } text) Edit(document, text);
    }
    public async Task CaptureAllAsync()
    {
        foreach (var document in _documents.Values.ToArray()) await CaptureAsync(document);
    }
    public Task SaveAsync(WorkspaceEditorDocument document) => ExecuteAsync(async () => { await CaptureAsync(document); await SaveCoreAsync([document]); });
    public Task SaveAllAsync() => ExecuteAsync(async () => { await CaptureAllAsync(); await SaveCoreAsync(_documents.Values.ToArray()); });

    private async Task SaveCoreAsync(IReadOnlyList<WorkspaceEditorDocument> documents)
    {
        var changes = documents.Where(document => document.Dirty).Select(document => (Document: document, File: new WorkspaceFile(document.Text))).ToArray();
        if (changes.Length == 0) return;
        if (IsLocal)
        {
            foreach (var (document, file) in changes)
            {
                try
                {
                    var saved = await NativeAsync<NativeWorkspaceFile>("workspace_write", new { path = document.Path, expectedHash = document.DiskHash, file, encoding = document.Encoding });
                    _workspace.Apply(Snapshot.Revision, [new(document.Path, Snapshot.Files.GetValueOrDefault(document.Path), saved.File)]);
                    _nativeFiles[document.Path] = saved;
                    document.SavedFile = saved.File; document.DiskHash = saved.Hash; document.Encoding = saved.Encoding; document.Error = null;
                }
                catch (Exception error) { document.Error = error.Message; throw; }
            }
        }
        else
        {
            await ApplyBrowserAsync(changes.Select(change => new WorkspaceChange(change.Document.Path, Snapshot.Files.GetValueOrDefault(change.Document.Path), change.File)).ToArray());
            foreach (var (document, file) in changes) { document.SavedFile = file; document.Error = null; }
        }
        Inspect(); EvaluatedGraph = null;
    }

    public async Task<bool> PrepareCloseAsync(WorkspaceEditorDocument document)
    {
        await CaptureAsync(document);
        if (!document.Dirty) return true;
        var choice = await _module!.InvokeAsync<string>("confirmDocumentClose", document.Path);
        if (choice == "cancel") return false;
        if (choice == "save") { await SaveAsync(document); return !document.Dirty; }
        document.Text = document.SavedFile.Content; document.Error = null; Notify(); return true;
    }

    public Task ReloadDocumentAsync(WorkspaceEditorDocument document) => ExecuteAsync(async () =>
    {
        if (document.Dirty && !await _module!.InvokeAsync<bool>("confirmDiscard", "Discard unsaved changes to " + document.Path + "?")) return;
        var file = document.SavedFile;
        if (IsLocal)
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path = document.Path });
            _nativeFiles[document.Path] = disk; file = disk.File;
            document.DiskHash = disk.Hash; document.Encoding = disk.Encoding;
            _workspace.Apply(Snapshot.Revision, [new(document.Path, Snapshot.Files.GetValueOrDefault(document.Path), file)]);
        }
        document.SavedFile = file; document.Text = file.Content; document.Error = null; Inspect();
    });

    public Task AddFileAsync(string path, string text = "") => ExecuteAsync(async () =>
    {
        path = WorkspacePath.Normalize(path);
        if (Inventory.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("The path already exists.");
        var file = new WorkspaceFile(text);
        if (IsLocal)
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_write", new { path, expectedHash = (string?)null, file });
            _nativeFiles[path] = disk; _workspace.Apply(Snapshot.Revision, [new(path, null, disk.File)]);
            _inventory = [.. _inventory, new(path, false, file.Content.Length)];
        }
        else await ApplyBrowserAsync([new(path, null, file)]);
        Inspect();
    });

    public Task MoveFileAsync(string path, string destination) => ExecuteAsync(async () =>
    {
        destination = WorkspacePath.Normalize(destination);
        if (_documents.Values.Any(document => document.Path == path && document.Dirty)) throw new InvalidOperationException("Save or discard the document before moving it.");
        if (WorkspacePath.IsEntry(path)) throw new InvalidOperationException("Rename solution/project paths in their source files and update their references explicitly; a raw file move cannot safely rewrite arbitrary imports.");
        if (IsLocal)
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path });
            await NativeAsync<JsonElement>("workspace_move", new { path, destination, expectedHash = disk.Hash });
            _inventory = await NativeAsync<WorkspaceInventoryFile[]>("workspace_list", new { });
            if (Snapshot.Files.ContainsKey(path)) _workspace.Move(Snapshot.Revision, path, destination);
            _nativeFiles.Remove(path);
        }
        else
        {
            var file = Snapshot.Files.GetValueOrDefault(path) ?? throw new FileNotFoundException("File not found.", path);
            await ApplyBrowserAsync([new(path, file, null), new(destination, null, file)]);
        }
        RemoveDocuments(path); Inspect();
        Output = "Moved the file. Explicit Include/Link/DependentUpon items and XAML resource URIs may require corresponding source edits.";
    });

    public Task DeleteFileAsync(string path) => ExecuteAsync(async () =>
    {
        if (_documents.Values.Any(document => document.Path == path && document.Dirty)) throw new InvalidOperationException("Save or discard the document before deleting it.");
        if (!await _module!.InvokeAsync<bool>("confirmDiscard", "Delete " + path + "? This does not update project or solution references.")) return;
        if (IsLocal)
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path });
            await NativeAsync<JsonElement>("workspace_delete", new { path, expectedHash = disk.Hash });
            _inventory = _inventory.Where(file => file.Path != path).ToArray(); _nativeFiles.Remove(path);
            if (Snapshot.Files.TryGetValue(path, out var previous)) _workspace.Apply(Snapshot.Revision, [new(path, previous, null)]);
        }
        else await ApplyBrowserAsync([new(path, Snapshot.Files.GetValueOrDefault(path), null)]);
        RemoveDocuments(path); Inspect();
    });

    public Task SetStartupAsync(string path) => ExecuteAsync(async () =>
    {
        _workspace.SetStartupProject(Snapshot.Revision, path);
        if (!IsLocal) await PersistBrowserAsync(Snapshot.Files, Snapshot.EntryPath);
    });

    public Task EditProjectAsync(string path, string operation, string name, string? value = null) => ExecuteAsync(async () =>
    {
        await CaptureAllAsync(); await SaveCoreAsync(_documents.Values.ToArray());
        if (IsLocal && !Snapshot.Files.ContainsKey(path))
        {
            var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path }); _nativeFiles[path] = disk;
            _workspace.Apply(Snapshot.Revision, [new(path, null, disk.File)]);
        }
        WorkspaceChange change = operation switch
        {
            "property" => ProjectFileEditor.SetProperty(Snapshot, path, name, value ?? ""),
            "package" => ProjectFileEditor.AddPackageReference(Snapshot, path, name, value),
            "reference" => ProjectFileEditor.AddProjectReference(Snapshot, path, name),
            "solution-add" => await SolutionFileService.AddProjectAsync(Snapshot, path, name, value),
            "solution-remove" => await SolutionFileService.RemoveProjectAsync(Snapshot, path, name),
            "solution-folder" => await SolutionFileService.AddFolderAsync(Snapshot, path, name),
            "configuration" => await SolutionFileService.AddConfigurationAsync(Snapshot, path, name, value ?? "Any CPU"),
            _ => throw new ArgumentException("Unknown project edit.")
        };
        if (IsLocal)
        {
            var hash = _nativeFiles.GetValueOrDefault(path)?.Hash ?? throw new InvalidOperationException("Read the file before editing it.");
            var saved = await NativeAsync<NativeWorkspaceFile>("workspace_write", new { path, expectedHash = hash, file = change.Replacement });
            _nativeFiles[path] = saved; _workspace.Apply(Snapshot.Revision, [change]);
        }
        else await ApplyBrowserAsync([change]);
        SynchronizeCleanDocuments(); Inspect(); EvaluatedGraph = null;
    });

    public Task RunSdkAsync(string operation) => ExecuteAsync(async () =>
    {
        RequireLocal(); RequireTrust(); await CaptureAllAsync(); await SaveCoreAsync(_documents.Values.ToArray());
        var path = Snapshot.EntryPath ?? throw new InvalidOperationException("Open a solution or project first.");
        if (operation == "evaluate")
        {
            EvaluatedGraph = await NativeAsync<JsonElement>("workspace_evaluate", new { path, trust = true, configuration = Configuration, platform = Platform, framework = Framework, includeCompilerDiagnostics = true });
            Output = JsonSerializer.Serialize(EvaluatedGraph, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            var result = await NativeAsync<JsonElement>("workspace_build", new { path, operation, trust = true, configuration = Configuration, platform = Platform, framework = Framework });
            SetCommandOutput(result); EvaluatedGraph = null;
        }
    });

    public Task LoadTemplatesAsync() => ExecuteAsync(async () =>
    {
        RequireLocal(); RequireTrust(); TemplateCatalog = await NativeAsync<JsonElement>("workspace_templates", new { trust = true });
        SetCommandOutput(TemplateCatalog.Value.GetProperty("command"));
    });
    public Task TemplateHelpAsync(string template) => ExecuteAsync(async () =>
    {
        RequireLocal(); RequireTrust(); SetCommandOutput(await NativeAsync<JsonElement>("workspace_template_help", new { template, trust = true }));
    });
    public Task InstallTemplatesAsync(string package, string version) => ExecuteAsync(async () =>
    {
        RequireLocal(); RequireTrust(); SetCommandOutput(await NativeAsync<JsonElement>("workspace_template_install", new { package, version, trust = true }));
        TemplateCatalog = null;
    });
    public Task CreateNativeAsync(object request) => ExecuteAsync(async () =>
    {
        RequireLocal(); RequireTrust();
        var result = await NativeAsync<JsonElement>("workspace_create", request);
        SetCommandOutput(result.GetProperty("command"));
        if (result.GetProperty("command").GetProperty("exitCode").GetInt32() != 0) return;
        _inventory = await NativeAsync<WorkspaceInventoryFile[]>("workspace_list", new { });
        await LoadNativeProjectFilesAsync();
        _workspace.Open(Snapshot.Revision, result.GetProperty("entryPath").GetString()!); Inspect();
    });

    public Task ExportAsync() => ExecuteAsync(async () =>
    {
        await CaptureAllAsync();
        var files = Snapshot.Files.ToBuilder();
        if (IsLocal)
            foreach (var entry in _inventory.Where(file => !file.IsDirectory))
            {
                var disk = await NativeAsync<NativeWorkspaceFile>("workspace_read", new { path = entry.Path });
                files[entry.Path] = disk.File;
            }
        foreach (var document in _documents.Values.Where(document => document.Dirty)) files[document.Path] = new(document.Text);
        var validated = new VirtualWorkspace(files, Snapshot.EntryPath);
        await _module!.InvokeVoidAsync("exportWorkspace", Path.GetFileNameWithoutExtension(Snapshot.EntryPath ?? "Workspace"),
            validated.Current.Files.Select(pair => new ImportedWorkspaceFile(pair.Key, pair.Value.Content, pair.Value.IsBinary)).ToArray());
        Output = "Exported source files as a ZIP archive, including unsaved buffers. Build output and version-control internals are excluded from native inventories.";
    });

    public async Task<bool> ConfirmReplaceAsync() => !HasUnsavedChanges || await _module!.InvokeAsync<bool>("confirmDiscard", "Discard unsaved workspace buffers and open another workspace?");
    public async Task EditorCommandAsync(WorkspaceEditorDocument document, string command)
    {
        if (command is "undo" or "redo") await _module!.InvokeVoidAsync("editorHistory", DocumentKey(document.Path), command);
        else { Error = "Workspace documents use normal text editing. Project-wide refactorings are not provided by structural inspection."; Notify(); }
    }

    private async Task ApplyBrowserAsync(IReadOnlyList<WorkspaceChange> changes)
    {
        var previous = Snapshot;
        var candidate = new VirtualWorkspace(previous.Files, previous.EntryPath);
        candidate.Apply(candidate.Current.Revision, changes);
        await PersistBrowserAsync(candidate.Current.Files, candidate.Current.EntryPath);
        _workspace.Apply(previous.Revision, changes);
    }
    private async Task ReplaceBrowserAsync(VirtualWorkspace candidate, string[] openDocuments)
    {
        var identity = Guid.NewGuid().ToString("N");
        var saved = new SavedSolutionWorkspace(1, _storageRevision, identity, candidate.Current.EntryPath, null,
            candidate.Current.Files.Select(pair => new ImportedWorkspaceFile(pair.Key, pair.Value.Content, pair.Value.IsBinary)).ToArray(), openDocuments);
        _storageRevision = await _module!.InvokeAsync<long>("saveWorkspace", saved, _storageRevision);
        _workspace = candidate; _identity = identity; _documents.Clear(); _nativeFiles.Clear(); _inventory = [];
        IsLocal = false; Trusted = false; EvaluatedGraph = null; SelectedProject = null; Inspect();
    }
    private async Task PersistBrowserAsync(ImmutableDictionary<string, WorkspaceFile> files, string? entry)
    {
        var saved = new SavedSolutionWorkspace(1, _storageRevision, _identity, entry, Snapshot.StartupProject,
            files.Select(pair => new ImportedWorkspaceFile(pair.Key, pair.Value.Content, pair.Value.IsBinary)).ToArray(), _documents.Values.Select(document => document.Path).Take(64).ToArray(), SelectedProject);
        _storageRevision = await _module!.InvokeAsync<long>("saveWorkspace", saved, _storageRevision);
    }
    private void SynchronizeCleanDocuments()
    {
        foreach (var document in _documents.Values.Where(document => !document.Dirty))
            if (Snapshot.Files.TryGetValue(document.Path, out var file))
            {
                document.SavedFile = file; document.Text = file.Content;
                if (_nativeFiles.TryGetValue(document.Path, out var disk)) { document.DiskHash = disk.Hash; document.Encoding = disk.Encoding; }
            }
    }
    private void Inspect()
    {
        try { Description = SolutionInspector.Inspect(Snapshot); }
        catch (Exception error) when (error is ArgumentException or System.Xml.XmlException or JsonException or IOException)
        { Description = new(Snapshot.EntryPath, "invalid", [], [], [], [error.Message, SolutionInspector.StructuralNotice]); }
    }
    private void RemoveDocuments(string path)
    {
        foreach (var id in _documents.Values.Where(document => document.Path == path).Select(document => document.Id).ToArray()) _documents.Remove(id);
    }
    private async Task<T> NativeAsync<T>(string action, object arguments) => await _module!.InvokeAsync<T>("nativeRequest", action, arguments);
    private void SetCommandOutput(JsonElement result)
    {
        Output = result.GetProperty("standardOutput").GetString() + "\n" + result.GetProperty("standardError").GetString() +
            "\nExit code: " + result.GetProperty("exitCode").GetInt32() +
            (result.GetProperty("outputTruncated").GetBoolean() ? "\n[Output capture limit reached]" : "");
        if (result.GetProperty("exitCode").GetInt32() != 0) Error = "The SDK operation failed. See Workspace output.";
    }
    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (_disposed || !Ready) return;
        if (!await _operations.WaitAsync(0)) { Error = "Another workspace operation is running."; Notify(); return; }
        Busy = true; Error = null; Notify();
        try { await operation(); }
        catch (Exception error) { Error = error.Message; }
        finally { Busy = false; _operations.Release(); Notify(); }
    }
    private void RequireLocal() { if (!IsLocal) throw new InvalidOperationException("Connect an opt-in local workspace first."); }
    private void RequireTrust() { if (!Trusted) throw new InvalidOperationException("Review and enable local SDK trust before executing project, package or template code."); }
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module != null)
        {
            try { await _module.InvokeVoidAsync("setUnsavedChanges", false); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
