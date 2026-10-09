using System.Text.Json;
using Avalonia.Styling;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;
using Dispatcher = Avalonia.Threading.Dispatcher;

namespace XamlG.Playground.Components;

public partial class IntelligentUiGuest
{
    [Parameter] public string Mode { get; set; } = "mcp";
    private readonly string _elementId = "ui-native-" + Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _mutations = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DotNetObjectReference<IntelligentUiGuest>? _reference;
    private IJSObjectReference? _module, _bridge;
    private UiAvaloniaRenderer? _renderer;
    private UiCSharpExpressionCompiler? _fullCompiler;
    private UiSessionStore? _executionStore;
    private UiSnapshot? _snapshot;
    private UiActionIntent? _review;
    private UiActionCall? _reviewCall;
    private string _status = "Starting native Avalonia…";
    private string? _error;
    private bool _disposed, _executed;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            await Preview.InitializeAsync(_elementId, new Uri(Navigation.BaseUri));
            _renderer = new(); _renderer.StateChanged += StateChanged; _renderer.ActionRequested += ActionRequested;
            await Preview.ShowAsync(_renderer.View);
            _reference = DotNetObjectReference.Create(this);
            _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", new Uri(new Uri(Navigation.BaseUri), "ui-native-guest.js").AbsoluteUri);
            _bridge = await _module.InvokeAsync<IJSObjectReference>("connect", _reference, Mode);
            _status = Mode == "mcp" ? "Native Avalonia connected; waiting for a UI result." : "Waiting for an explicitly approved C# declaration.";
            StateHasChanged();
        }
        catch (Exception error) { await ReceiveError(error.Message); }
    }
    [JSInvokable]
    public async Task ReceiveSnapshot(UiSnapshot snapshot)
    {
        if (_disposed || _renderer == null) return;
        if (snapshot.Id.Length > 80 || snapshot.SessionId.Length != 32 || snapshot.Revision < 1 || snapshot.StateRevision < 0) throw new UiException("invalid_snapshot", "Invalid UI identity.");
        if (_snapshot?.SessionId == snapshot.SessionId && (snapshot.Revision < _snapshot.Revision || snapshot.Revision == _snapshot.Revision && snapshot.StateRevision < _snapshot.StateRevision)) return;
        await Dispatcher.UIThread.InvokeAsync(() => _renderer.Apply(snapshot));
        _snapshot = snapshot; _review = null; _reviewCall = null;
        _status = $"Native Avalonia · revision {snapshot.Revision} · state {snapshot.StateRevision}";
        await InvokeAsync(StateHasChanged);
    }
    [JSInvokable]
    public Task ReceiveError(string message)
    { _error = message.Length > 4096 ? message[..4096] : message; return _disposed ? Task.CompletedTask : InvokeAsync(StateHasChanged); }
    [JSInvokable]
    public async Task HostContextChanged(JsonElement context)
    {
        if (context.ValueKind == JsonValueKind.Object && context.TryGetProperty("theme", out var theme))
            await Dispatcher.UIThread.InvokeAsync(() => { if (global::Avalonia.Application.Current is { } app) app.RequestedThemeVariant = theme.GetString() == "dark" ? ThemeVariant.Dark : ThemeVariant.Light; });
    }
    private async void StateChanged(UiStateChange change)
    {
        try
        {
            await _mutations.WaitAsync(_lifetime.Token);
            try
            {
                if (_snapshot == null || _snapshot.Id != change.Id || _snapshot.Revision != change.ExpectedRevision) throw new UiException("revision_conflict", "The UI changed before input was applied.");
                change = change with { ExpectedStateRevision = _snapshot.StateRevision };
                var next = Mode == "execution" ? _executionStore!.ChangeState(change, "approved-execution") : await _bridge!.InvokeAsync<UiSnapshot>("tool", _lifetime.Token, "xamlg_ui_state", change);
                await ReceiveSnapshot(next);
                if (Mode == "mcp") await _bridge!.InvokeVoidAsync("context", _lifetime.Token, new { surfaceId = next.Id, next.Revision, next.StateRevision, next.State });
            }
            finally { _mutations.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { await ReceiveError(error.Message); if (_snapshot != null && !_disposed) await ReceiveSnapshot(_snapshot); }
    }
    private async void ActionRequested(UiActionCall call)
    {
        try
        {
            if (Mode != "mcp") throw new UiException("action_disabled", "Full-C# preview has no editor or agent action authority.");
            _review = await _bridge!.InvokeAsync<UiActionIntent>("tool", _lifetime.Token, "xamlg_ui_action", call);
            _reviewCall = call; await InvokeAsync(StateHasChanged);
        }
        catch (Exception error) { await ReceiveError(error.Message); }
    }
    private void CancelReview() { _review = null; _reviewCall = null; }
    private async Task ConfirmAsync()
    {
        if (_review == null || _reviewCall == null || _bridge == null) return;
        var reviewed = _review; var call = _reviewCall; CancelReview();
        try
        {
            var current = await _bridge.InvokeAsync<UiActionIntent>("tool", _lifetime.Token, "xamlg_ui_action", call);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(current), JsonSerializer.SerializeToElement(reviewed))) throw new UiException("revision_conflict", "The action changed. Review it again.");
            await _bridge.InvokeVoidAsync("action", _lifetime.Token, current);
        }
        catch (Exception error) { await ReceiveError(error.Message); }
    }
    private async Task RefreshAsync()
    { try { if (_bridge != null) await _bridge.InvokeVoidAsync("refresh", _lifetime.Token); } catch (Exception error) { await ReceiveError(error.Message); } }
    [JSInvokable]
    public async Task<object> ExecuteApproved(UiPublish request)
    {
        if (Mode != "execution" || _executed || _disposed) throw new UiException("execution_not_approved", "Create a fresh reviewed execution frame.");
        _executed = true;
        await Compiler.InitializeAsync(cancellationToken: _lifetime.Token);
        var metadata = Compiler.Analyze("<StackPanel xmlns=\"https://github.com/avaloniaui\"/>", "", cancellationToken: _lifetime.Token).Compilation.References;
        _fullCompiler = new(metadata, _ => true, maximumCompilations: 64);
        _executionStore = new(new UiCompiler(expressionCompiler: _fullCompiler));
        var snapshot = _executionStore.Publish(request with { ExpectedRevision = 0, Sequence = 1, Actions = [] }, "approved-execution");
        await ReceiveSnapshot(snapshot);
        return new { snapshot.Id, snapshot.Revision, snapshot.StateRevision, snapshot.FallbackMarkdown, expressionLanguage = "csharp-full" };
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        if (_bridge != null) { try { await _bridge.InvokeVoidAsync("dispose"); } catch (JSDisconnectedException) { } await _bridge.DisposeAsync(); }
        if (_module != null) await _module.DisposeAsync(); _reference?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { _renderer?.Dispose(); _executionStore?.Clear(); _fullCompiler?.Dispose(); Preview.Dispose(); });
        _lifetime.Dispose();
    }
}
