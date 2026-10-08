using Dockyard.Blazor;
using Microsoft.JSInterop;

namespace XamlG.Playground;

public partial class App
{
    private readonly string _panePrefix = "xamlg-" + Guid.NewGuid().ToString("N") + "-";
    private DockingManager _dock = default!;
    private bool _dockReady;
    private string? _defaultLayout;
    private string PaneTemplate(string id) => _panePrefix + id;

    private async Task InitializeDockyardAsync(IJSObjectReference manager)
    {
        try
        {
            DockContent Content(string id, string title) => new()
            {
                ContentId = id, Title = title, CanClose = false,
                Content = BrowserFunction.RazorTemplate(PaneTemplate(id), fields: ["ContentId", "Title"])
            };
            await using var explorer = await _dock.AddAnchorableAsync(Content("explorer", "Explorer"), "Left");
            await using var source = await _dock.AddDocumentAsync(Content("source", "Source"));
            await using var preview = await _dock.AddAnchorableAsync(Content("preview", "Avalonia preview"), "Right");
            await using var problems = await _dock.AddAnchorableAsync(Content("problems", "Problems"), "Bottom");
            await using var inspector = await _dock.AddAnchorableAsync(Content("inspector", "Inspectors"), "Bottom");
            await using var agent = await _dock.AddAnchorableAsync(Content("agent", "Coding agent"), "Right");
            await using var explorerPane = await _dock.Module!.GetAsync<IJSObjectReference>(explorer, "Parent");
            await using var previewPane = await _dock.Module.GetAsync<IJSObjectReference>(preview, "Parent");
            await using var inspectorPane = await _dock.Module.GetAsync<IJSObjectReference>(inspector, "Parent");
            await _dock.Module.SetAsync(explorerPane, "DockWidth", 210);
            await _dock.Module.SetAsync(previewPane, "DockWidth", 450);
            await _dock.Module.SetAsync(inspectorPane, "DockHeight", 280);
            await _dock.ActivateAsync(preview);
            _defaultLayout = await _dock.SaveLayoutAsync();
            _dockReady = true;
            // Content factories are registered before restoration so serialized layouts always
            // rebind to the current interactive Razor panes, without serializing their state.
            await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
            var saved = await module.InvokeAsync<string?>("loadDockyardLayout");
            if (saved != null)
            {
                try { await _dock.LoadLayoutAsync(saved); }
                catch { await _dock.LoadLayoutAsync(_defaultLayout); }
            }
        }
        catch (Exception error) { Report(error); }
        StateHasChanged();
    }

    private async Task DockyardChangedAsync(BrowserEvent args)
    {
        if (!_dockReady || _disposed || _module == null) return;
        try { await _module.InvokeVoidAsync("saveDockyardLayout", await _dock.SaveLayoutAsync()); }
        catch (JSDisconnectedException) { }
    }

    private async Task ResetDockyardAsync()
    {
        if (_dockReady && _defaultLayout != null) await _dock.LoadLayoutAsync(_defaultLayout);
    }

    private async Task ShowPaneAsync(string id)
    {
        await using var pane = await _dock.FindAsync(id);
        // Source is a non-closeable LayoutDocument; only anchorable tool panes
        // expose Show. Activating the document also restores its editor focus.
        if (id != "source") await _dock.Module!.CallVoidAsync(pane, "Show");
        await _dock.ActivateAsync(pane);
    }
}
