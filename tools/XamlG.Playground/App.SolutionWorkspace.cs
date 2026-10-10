using Dockyard.Blazor;
using Microsoft.JSInterop;
using XamlG.Playground.Workspaces;

namespace XamlG.Playground;

public partial class App
{
    private readonly SolutionWorkspaceSession _solutionSession = new();
    private readonly HashSet<string> _knownWorkspaceDocuments = new(StringComparer.Ordinal);
    private string? _activeWorkspaceDocumentId;
    private bool _solutionWizardVisible;
    private string _solutionWizardMode = "solution";
    private bool WorkspaceActive => _activeWorkspaceDocumentId != null && _solutionSession.Documents.ContainsKey(_activeWorkspaceDocumentId);
    private WorkspaceEditorDocument? ActiveWorkspaceDocument => _activeWorkspaceDocumentId != null ? _solutionSession.Documents.GetValueOrDefault(_activeWorkspaceDocumentId) : null;
    private string[] AllDocumentIds() => _documentBuffers.Keys.Concat(_solutionSession.Documents.Keys).ToArray();

    private async Task SolutionWorkspaceChangedAsync()
    {
        if (_disposed) return;
        if (!_knownWorkspaceDocuments.SetEquals(_solutionSession.Documents.Keys))
        {
            _knownWorkspaceDocuments.Clear(); _knownWorkspaceDocuments.UnionWith(_solutionSession.Documents.Keys);
            _documentsNeedReconcile = true;
            if (_activeWorkspaceDocumentId != null && !_solutionSession.Documents.ContainsKey(_activeWorkspaceDocumentId)) _activeWorkspaceDocumentId = null;
        }
        StateHasChanged();
        if (_dockReady && _module != null) await ReconcileDockDocumentsAsync();
    }

    private async Task OpenWorkspaceDocumentAsync(WorkspaceEditorDocument document)
    {
        if (!_dockReady || _module == null) return;
        if ((await DockContentsAsync()).All(pane => pane.Id != document.Id))
        {
            await using var pane = await _dock.AddDocumentAsync(PaneContent(document.Id, document.Path, "workspace-document"));
            _registeredDocuments.Add(document.Id);
        }
        _activeWorkspaceDocumentId = document.Id;
        await _module.InvokeVoidAsync("activateDockContent", _dockManager, document.Id, true);
        StateHasChanged();
    }

    private async Task OpenWorkspaceFileAsync(string path)
    {
        if (await _solutionSession.OpenDocumentAsync(path) is { } document) await OpenWorkspaceDocumentAsync(document);
    }

    private async Task RegisterWorkspaceDocumentsAsync()
    {
        foreach (var document in _solutionSession.Documents.Values.Where(document => !_registeredDocuments.Contains(document.Id)).ToArray())
        {
            await using var pane = await _dock.AddDocumentAsync(PaneContent(document.Id, document.Path, "workspace-document"));
            _registeredDocuments.Add(document.Id);
        }
    }

    private Task ShowWorkspaceOutputAsync() => ShowPaneAsync("workspace-output");
    private async Task RunWorkspaceCommandAsync(string operation)
    {
        await ShowWorkspaceOutputAsync();
        await _solutionSession.RunSdkAsync(operation);
    }
    private Task ShowSolutionWizardAsync(string mode)
    {
        if (!_solutionSession.Ready || _solutionSession.Busy) return Task.CompletedTask;
        _solutionWizardMode = mode; _solutionWizardVisible = true;
        StateHasChanged(); return Task.CompletedTask;
    }
    private void CloseSolutionWizard() { _solutionWizardVisible = false; StateHasChanged(); }
    private async Task SolutionCreatedAsync()
    {
        _solutionWizardVisible = false;
        if (_solutionSession.Snapshot.EntryPath is { } entry) await OpenWorkspaceFileAsync(entry);
        var source = _solutionSession.Inventory.Where(file => !file.IsDirectory).Select(file => file.Path)
            .FirstOrDefault(path => path.EndsWith("/MainWindow.axaml", StringComparison.OrdinalIgnoreCase))
            ?? _solutionSession.Inventory.FirstOrDefault(file => !file.IsDirectory && file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))?.Path;
        if (source != null) await OpenWorkspaceFileAsync(source);
        await ShowPaneAsync("explorer", focus: false);
        StateHasChanged();
    }
    private async Task<bool> PrepareSolutionDocumentCloseAsync(string id)
    {
        if (!_solutionSession.Documents.TryGetValue(id, out var document)) return true;
        return await _solutionSession.PrepareCloseAsync(document);
    }
}
