using System.Globalization;
using System.Text.Json;
using Avalonia.Browser;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;
using Dispatcher = Avalonia.Threading.Dispatcher;

namespace XamlG.Playground.Components;

public partial class IntelligentUiCard
{
    [Parameter, EditorRequired] public UiSessionStore Store { get; set; } = default!;
    [Parameter, EditorRequired] public UiPresentation Presentation { get; set; } = default!;
    [Parameter] public bool Ready { get; set; }
    [Parameter] public EventCallback<string> MessageRequested { get; set; }
    [Parameter] public Func<UiActionCall, CancellationToken, Task<JsonElement>>? ToolRequested { get; set; }
    [Parameter] public IReadOnlyList<AutomationTool> Tools { get; set; } = [];
    private readonly string _elementId = "intelligent-ui-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private UiAvaloniaSession? _session;
    private UiSessionStore? _mountedStore;
    private AvaloniaView? _view;
    private IJSObjectReference? _module;
    private UiSnapshot? _snapshot;
    private UiActionCall? _action;
    private UiActionIntent? _intent;
    private AutomationTool? _reviewedTool;
    private string? _mountedId, _failedMountId, _error, _notice;
    private bool _disposed, _mounting, _busy, _inspect;
    private long _inputRevision;
    private AutomationTool? ReviewedTool => _intent?.Tool is { } name ? Tools.SingleOrDefault(tool => tool.Name == name) : null;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _mounting || !Ready || _failedMountId == Presentation.SessionId) return;
        if (_session != null && ReferenceEquals(_mountedStore, Store) && _mountedId == Presentation.SessionId) return;
        _mounting = true;
        var mountingId = Presentation.SessionId;
        try
        {
            await ReleaseNativeAsync();
            _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", "./intelligent-ui.js");
            if (_disposed) return;
            var store = Store; var presentation = Presentation;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                _session = UiAvaloniaSession.CreateLocal(store, presentation);
                _session.Updated += OnUpdated; _session.Failed += OnFailed; _session.ActionRequested += OnAction;
                _view = new AvaloniaView(_elementId) { Content = _session.View };
                _mountedStore = store; _mountedId = presentation.SessionId; _snapshot = _session.Snapshot;
            });
            if (!_disposed) StateHasChanged();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _failedMountId = mountingId; _error = error.Message;
            await ReleaseNativeAsync();
            if (!_disposed) StateHasChanged();
        }
        finally { _mounting = false; }
    }
    private void RetryNativeView() { _failedMountId = null; _error = null; }
    private void OnUpdated()
    {
        if (_disposed) return; _snapshot = _session?.Snapshot;
        if (_action != null && (_snapshot == null || _snapshot.Revision != _action.ExpectedRevision || _snapshot.StateRevision != _action.ExpectedStateRevision)) CancelAction();
        _ = InvokeAsync(StateHasChanged);
    }
    private void OnFailed(string message) { if (!_disposed) { _error = message; _inputRevision++; _ = InvokeAsync(StateHasChanged); } }
    private void OnAction(UiActionCall call)
    {
        if (_disposed || _busy) return;
        _ = InvokeAsync(() =>
        {
            try
            {
                _intent = Store.PrepareActionLocal(call); _action = call; _reviewedTool = ReviewedTool;
                _error = null; _notice = null;
            }
            catch (UiException error) { _error = error.Message; }
            StateHasChanged();
        });
    }
    private bool HasInput(string key) => _snapshot != null && UiSessionStore.Flatten(_snapshot.Roots).Any(node => node.StateKey == key);
    private static string StateText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private async Task ChangeStateAsync(string key, ChangeEventArgs args)
    {
        if (_busy || _snapshot == null || _session == null) return;
        var current = _snapshot;
        try
        {
            var old = current.State.GetProperty(key);
            var value = old.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => AutomationJson.Element(args.Value is true),
                JsonValueKind.Number => AutomationJson.Element(decimal.Parse(args.Value?.ToString() ?? "", NumberStyles.Float, CultureInfo.InvariantCulture)),
                _ => AutomationJson.Element(args.Value?.ToString() ?? "")
            };
            _error = null;
            await Dispatcher.UIThread.InvokeAsync(() => _session?.ChangeState(new(current.Id, current.Revision, current.StateRevision, key, value)));
        }
        catch (Exception error) when (error is UiException or FormatException or OverflowException) { _error = error.Message; }
        finally { _inputRevision++; }
    }
    private void CancelAction() { _action = null; _intent = null; _reviewedTool = null; }
    private async Task ConfirmAsync()
    {
        if (_busy || _action == null || _intent == null) return;
        var call = _action; var reviewed = _intent; _busy = true; _error = null;
        try
        {
            var current = Store.PrepareActionLocal(call);
            if (JsonSerializer.Serialize(current, AutomationJson.Options) != JsonSerializer.Serialize(reviewed, AutomationJson.Options)) throw new UiException("revision_conflict", "The action changed. Review it again.");
            switch (current.Kind)
            {
                case "message": await MessageRequested.InvokeAsync(current.Text!); _notice = "Message copied into the composer; it has not been sent."; break;
                case "copy": await CopyAsync(current.Text!); _notice = "Copied."; break;
                case "openUrl": await ModuleAsync(); await _module!.InvokeVoidAsync("openLink", _lifetime.Token, current.Text!); _notice = "Requested opening the reviewed link."; break;
                case "tool":
                    if (ToolRequested == null || ReviewedTool == null || ReviewedTool != _reviewedTool) throw new UiException("tool_changed", "The tool definition changed or is unavailable. Review it again.");
                    var result = await ToolRequested(call, _lifetime.Token);
                    _notice = "Tool completed. " + (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("revision", out var revision) ? "Revision " + revision : "");
                    break;
                default: throw new UiException("invalid_action", "Unsupported action.");
            }
            CancelAction();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed) _error = error.Message; }
        finally { _busy = false; }
    }
    private async Task ModuleAsync() { _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "./intelligent-ui.js"); }
    private async Task CopyAsync(string text) { await ModuleAsync(); await _module!.InvokeVoidAsync("copyText", _lifetime.Token, text); }
    private Task ExportXamlAsync() => DownloadAsync("IntelligentView.axaml", "application/xml", _snapshot == null ? "" : UiSourceExporter.Xaml(_snapshot));
    private Task ExportCSharpAsync() => DownloadAsync("GeneratedIntelligentView.cs", "text/plain", _snapshot == null ? "" : UiSourceExporter.CSharp(_snapshot));
    private Task ExportSnapshotAsync() => DownloadAsync("intelligent-ui.json", "application/json", JsonSerializer.Serialize(_snapshot, AutomationJson.Options));
    private async Task DownloadAsync(string name, string mime, string text)
    {
        try { await ModuleAsync(); await _module!.InvokeVoidAsync("downloadText", _lifetime.Token, name, mime, text); }
        catch (Exception error) when (error is not OutOfMemoryException) { _error = error.Message; }
    }
    private async Task ReleaseNativeAsync()
    {
        if (_session == null && _view == null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_view != null) _view.Content = null;
            _session?.Dispose(); _session = null;
            if ((object?)_view is IDisposable disposable) disposable.Dispose();
            _view = null; _snapshot = null; _mountedId = null; _mountedStore = null; CancelAction();
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        await ReleaseNativeAsync();
        if (_module != null) { try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { } }
        _lifetime.Dispose();
    }
}
