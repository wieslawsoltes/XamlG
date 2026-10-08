using Dockyard.Blazor;
using Microsoft.JSInterop;
using XamlG.Playground.Components;

namespace XamlG.Playground;

public partial class App
{
    private static readonly ToolPane[] ToolPanes =
    [
        new("explorer", "Explorer", "Left"), new("preview", "Avalonia preview", "Right"),
        new("problems", "Problems"), new("generated", "C# output"), new("code-files", "C# files"),
        new("resources", "Resources"), new("syntax", "Syntax"), new("bound-tree", "Bound tree"),
        new("visual-tree", "Visual tree"), new("properties", "Properties"), new("designer", "Designer"),
        new("runtime", "Runtime properties"), new("compiler", "Compiler"), new("pipeline", "Pipeline"),
        new("runtime-objects", "Runtime objects", DefaultOpen: false), new("runtime-bindings", "Bindings", DefaultOpen: false),
        new("runtime-styles", "Styles", DefaultOpen: false), new("runtime-resources", "Runtime resources", DefaultOpen: false),
        new("runtime-events", "Events", DefaultOpen: false), new("runtime-input", "Input", DefaultOpen: false),
        new("runtime-accessibility", "Accessibility", DefaultOpen: false), new("runtime-tools", "Runtime tools", DefaultOpen: false),
        new("agent", "Coding agent", "Right"), new("agent-access", "Agent access", "Right")
    ];
    private readonly string _panePrefix = "xamlg-" + Guid.NewGuid().ToString("N") + "-";
    private DockingManager _dock = default!;
    private IJSObjectReference? _dockManager, _dockHooks, _shellHooks;
    private readonly HashSet<string> _registeredDocuments = new(StringComparer.Ordinal);
    private DotNetObjectReference<App>? _dockReference;
    private AgentWorkbench? _agentWorkbench;
    private bool _dockReady, _reconcilingDocuments;
    private string? _defaultLayout;
    private string? _initialDockLayout;
    private bool _initialLayoutRestored;
    private string PaneTemplate(string id) => _panePrefix + id;
    private DockContent PaneContent(string id, string title, string template) => new()
    {
        ContentId = id, Title = title, CanClose = id != "preview",
        Content = BrowserFunction.RazorTemplate(PaneTemplate(template is "explorer" or "preview" or "problems" or "agent" or "agent-access" or "document" ? template : "inspector"), fields: ["ContentId", "Title"])
    };
    private async Task InitializeDockyardAsync(IJSObjectReference manager)
    {
        try
        {
            _dockManager = manager;
            await RestoreInitialStateAsync();
            ReconcileSourceBuffers();
            foreach (var buffer in _documentBuffers.Values)
            { await using var document = await _dock.AddDocumentAsync(PaneContent(buffer.Id, buffer.Path, "document")); _registeredDocuments.Add(buffer.Id); }
            foreach (var tool in ToolPanes)
            {
                await using var pane = await _dock.AddAnchorableAsync(PaneContent(tool.Id, tool.Title, tool.Id), tool.Side);
                if (!tool.DefaultOpen) await _dock.InvokeAsync<bool>("Close", pane);
                if (tool.Id == "preview") await _dock.Module!.SetAsync(pane, "CanHide", false);
                if (tool.Id is "explorer" or "preview" or "problems")
                {
                    await using var parent = await _dock.Module!.GetAsync<IJSObjectReference>(pane, "Parent");
                    await _dock.Module.SetAsync(parent, tool.Side == "Bottom" ? "DockHeight" : "DockWidth", tool.Id == "explorer" ? 210 : tool.Id == "preview" ? 450 : 280);
                }
            }
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "studio.js");
            await module.InvokeVoidAsync("activateDockContent", manager, "preview", true);
            await module.InvokeVoidAsync("activateDockContent", manager, "problems", true);
            await module.InvokeVoidAsync("activateDockContent", manager, DocumentId("View.axaml"), true);
            _defaultLayout = await _dock.SaveLayoutAsync();
            var saved = await module.InvokeAsync<string?>("loadDockyardLayout");
            _initialDockLayout = saved;
            if (saved != null)
            {
                try
                {
                    var layout = await module.InvokeAsync<string>("filterDockyardLayout", saved, AllowedDockIds());
                    await _dock.LoadLayoutAsync(layout);
                }
                catch { await _dock.LoadLayoutAsync(_defaultLayout); }
            }
            _dockReference = DotNetObjectReference.Create(this);
            _dockHooks = await module.InvokeAsync<IJSObjectReference>("installDockyardWorkspace", manager, _dockReference);
            _dockReady = true;
        }
        catch (Exception error) { Report(error); }
        StateHasChanged();
    }
    private string[] AllowedDockIds() => ToolPanes.Select(tool => tool.Id).Concat(_documentBuffers.Keys).ToArray();
    private async Task RestoreInitialDockLayoutAsync()
    {
        if (!_dockReady || _initialLayoutRestored || _module == null) return;
        ReconcileSourceBuffers(); ReconcileGeneratedBuffers();
        await ReconcileDockDocumentsAsync();
        if (_initialDockLayout != null)
        {
            var layout = await _module.InvokeAsync<string>("filterDockyardLayout", _initialDockLayout, AllowedDockIds());
            await _dock.LoadLayoutAsync(layout);
        }
        _initialLayoutRestored = true;
    }
    private async Task<DockPaneState[]> DockContentsAsync() => _module == null || _dockManager == null ? [] :
        await _module.InvokeAsync<DockPaneState[]>("dockyardContents", _dockManager);
    private async Task DockyardChangedAsync(BrowserEvent args)
    {
        if (!_dockReady || !_initialLayoutRestored || _disposed || _module == null) return;
        try { await _module.InvokeVoidAsync("saveDockyardLayout", await _dock.SaveLayoutAsync()); }
        catch (JSDisconnectedException) { }
    }
    [JSInvokable]
    public void DockContentActivated(string id)
    {
        if (_disposed) return;
        if (_documentBuffers.TryGetValue(id, out var buffer)) _activeDocumentPath = buffer.Path;
        else if (ToolPanes.FirstOrDefault(tool => tool.Id == id) is { } tool) _inspectorTab = tool.Title;
        StateHasChanged();
    }
    private async Task LoadDockyardLayoutAsync(string layout)
    {
        await CaptureEditorsAsync(); ReconcileSourceBuffers(); ReconcileGeneratedBuffers();
        // Register factories for unopened documents before hydrating their saved identities.
        foreach (var buffer in _documentBuffers.Values.Where(buffer => !_registeredDocuments.Contains(buffer.Id)).ToArray())
        {
            await using var pane = await _dock.AddDocumentAsync(PaneContent(buffer.Id, buffer.Path, "document"));
            _registeredDocuments.Add(buffer.Id);
        }
        var filtered = await _module!.InvokeAsync<string>("filterDockyardLayout", layout, AllowedDockIds());
        await _dock.LoadLayoutAsync(filtered);
    }
    private async Task ResetDockyardAsync()
    {
        if (!_dockReady || _defaultLayout == null) return;
        await CaptureEditorsAsync();
        await _dock.LoadLayoutAsync(_defaultLayout);
    }
    private async Task EnsureDocumentPaneAsync(DocumentBuffer buffer, bool focus)
    {
        if ((await DockContentsAsync()).All(pane => pane.Id != buffer.Id))
        { await using var pane = await _dock.AddDocumentAsync(PaneContent(buffer.Id, buffer.Path, "document")); _registeredDocuments.Add(buffer.Id); }
        await _module!.InvokeVoidAsync("activateDockContent", _dockManager, buffer.Id, focus);
    }
    private async Task ReconcileDockDocumentsAsync()
    {
        if (!_documentsNeedReconcile || !_dockReady || _reconcilingDocuments || _module == null) return;
        _reconcilingDocuments = true; _documentsNeedReconcile = false;
        try { await _module.InvokeVoidAsync("reconcileDockDocuments", _dockManager, _documentBuffers.Keys.ToArray(), _registeredDocuments.ToArray()); _registeredDocuments.IntersectWith(_documentBuffers.Keys); }
        finally { _reconcilingDocuments = false; }
    }
    private Task ShowInspectorAsync(string title) => ShowPaneAsync(ToolPanes.Single(tool => tool.Title == (title == "Runtime" ? "Runtime properties" : title)).Id);
    private async Task ShowPaneAsync(string id, bool focus = true)
    {
        if (!_dockReady || _module == null) return;
        if (id == "source") { await OpenDocumentAsync(_activeDocumentPath); return; }
        if (id == "inspector") { await ShowInspectorAsync(_inspectorTab); return; }
        var tool = ToolPanes.SingleOrDefault(tool => tool.Id == id) ?? throw new ArgumentException("Unknown tool window.");
        if ((await DockContentsAsync()).All(pane => pane.Id != id))
        { await using var pane = await _dock.AddAnchorableAsync(PaneContent(id, tool.Title, id), tool.Side); }
        await _module.InvokeVoidAsync("activateDockContent", _dockManager, id, focus);
        _inspectorTab = tool.Title;
    }
    private sealed record ToolPane(string Id, string Title, string Side = "Bottom", bool DefaultOpen = true);
    private sealed record DockPaneState(string Id, string Title, bool Active, bool Hidden);
}
