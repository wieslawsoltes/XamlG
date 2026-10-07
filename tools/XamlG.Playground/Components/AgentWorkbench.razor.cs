using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly string[] Profiles = ["ask", "readOnly", "plan", "autoEdit", "fullAccess", "custom"];
    private static readonly string[] Scopes = ["project", "source", "designer", "compiler", "runtime", "layout", "build", "agent"];
    private readonly Dictionary<string, string> _scopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _restorePaths = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private IJSObjectReference? _module;
    private DotNetObjectReference<AgentWorkbench>? _reference;
    private WorkbenchState _state = new();
    private string _provider = "", _model = "", _name = "New task", _taskName = "", _selectedId = "", _draft = "", _answer = "", _liveText = "";
    private string _profile = "ask", _toolRules = "{}";
    private string[] _models = [];
    private FileChange[] _changePreview = [];
    private string? _error;
    private bool _connected, _refreshing, _disposed, _neverAsk, _autoCompact = true;
    private int _requests = 128, _tools = 1024, _outputTokens = 32768, _contextBytes = 6_000_000, _leaseMinutes = 10;
    private long _taskTokens = 4_000_000;
    private TaskView? Selected => _state.Tasks.FirstOrDefault(task => task.Id == _selectedId);
    private static bool IsRunning(TaskView task) => task.Status is "running" or "awaitingApproval" or "awaitingAnswer";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
        _reference = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("installAgentWorkbench", _reference, _ownerId);
        await RefreshAsync(); _ = PollAsync();
    }
    private async Task PollAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(_lifetime.Token)) await InvokeAsync(RefreshAsync);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    [JSInvokable] public Task AgentRefresh() => RefreshAsync();
    [JSInvokable] public void AgentDisconnected() { _connected = false; _liveText = ""; StateHasChanged(); }
    [JSInvokable] public void AgentStreamError(string message) { _error = message; StateHasChanged(); }
    [JSInvokable] public async Task AgentStream(EventView item)
    {
        if (item.TaskId == _selectedId && item.Kind == "text_delta")
        { _liveText += item.Text; if (_liveText.Length > 262144) _liveText = _liveText[^262144..]; StateHasChanged(); }
        else
        { if (item.TaskId == _selectedId && item.Kind is "assistant" or "completed" or "failed" or "paused") _liveText = ""; await RefreshAsync(); }
    }
    private async Task RefreshAsync()
    {
        if (_module == null || _refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            _connected = await _module.InvokeAsync<bool>("agentConnected");
            if (_connected)
            {
                _state = await RequestAsync<WorkbenchState>("state", new { });
                if (!_state.Providers.Contains(_provider)) _provider = _state.Providers.FirstOrDefault() ?? "";
                if (Selected == null) Select(_state.Tasks.FirstOrDefault()?.Id ?? "");
            }
        }
        catch (JSException error) { _error = error.Message; }
        finally { _refreshing = false; if (!_disposed) StateHasChanged(); }
    }
    private ValueTask<T> RequestAsync<T>(string action, object arguments) => _module!.InvokeAsync<T>("agentRequest", action, arguments);
    private async Task CommandAsync(string action, object arguments)
    {
        try { _error = null; await RequestAsync<JsonElement>(action, arguments); await RefreshAsync(); }
        catch (Exception error) when (error is JSException or JsonException or ArgumentException) { _error = error.Message; }
    }
    private void Select(string id)
    { _selectedId = id; _draft = Selected?.Draft ?? ""; _taskName = Selected?.Name ?? ""; _liveText = ""; _changePreview = []; _restorePaths.Clear(); }
    private Task SelectTaskAsync(ChangeEventArgs args) { Select(args.Value?.ToString() ?? ""); return Task.CompletedTask; }
    private async Task DraftChangedAsync(ChangeEventArgs args)
    {
        _draft = args.Value?.ToString() ?? "";
        if (Selected != null)
            try { await RequestAsync<JsonElement>("draft", new { id = _selectedId, text = _draft }); }
            catch (JSException error) { _error = error.Message; }
    }
    private async Task CreateAsync()
    {
        try
        {
            _error = null; var task = await RequestAsync<TaskView>("create", new { name = _name, provider = _provider, model = _model });
            await RefreshAsync(); Select(task.Id);
        }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task DiscoverModelsAsync()
    { try { _error = null; _models = await RequestAsync<string[]>("models", new { provider = _provider }); } catch (JSException error) { _error = error.Message; } }
    private object Options() => new
    {
        policy = new { profile = _profile, scopes = _scopes, tools = JsonSerializer.Deserialize<Dictionary<string, string>>(_toolRules), neverAsk = _neverAsk },
        limits = new { requestsPerRun = _requests, toolsPerRun = _tools, outputTokensPerRequest = _outputTokens, totalTaskTokens = _taskTokens, contextBytes = _contextBytes },
        leaseDuration = TimeSpan.FromMinutes(_leaseMinutes), automaticCompaction = _autoCompact
    };
    private async Task StartAsync(string? message)
    {
        try { await CommandAsync("run", new { id = _selectedId, message, options = Options() }); }
        catch (Exception error) when (error is JsonException or ArgumentException) { _error = error.Message; }
    }
    private Task RunAsync() => StartAsync(_draft);
    private Task ResumeAsync() => StartAsync(null);
    private Task RespondAsync(string id, string value) => CommandAsync("respond", new { id, value });
    private void ChangeScope(string scope, ChangeEventArgs args)
    { var value = args.Value?.ToString() ?? "default"; if (value == "default") _scopes.Remove(scope); else _scopes[scope] = value; }
    private void SelectRestore(string path, ChangeEventArgs args)
    { if (args.Value is true) _restorePaths.Add(path); else _restorePaths.Remove(path); }
    private async Task PreviewChangesAsync()
    { try { _changePreview = (await RequestAsync<ChangeView>("changes", new { id = _selectedId })).Files; } catch (JSException error) { _error = error.Message; } }
    private Task RestoreAsync() => CommandAsync("restore", new { id = _selectedId, paths = _restorePaths.ToArray(), expectedRevision = Selected!.Changes!.Revision });
    private async Task ExportAsync()
    {
        try
        {
            var text = await RequestAsync<string>("export", new { id = _selectedId });
            await _module!.InvokeVoidAsync("download", "xamlg-agent-thread.json", text, "application/json");
        }
        catch (JSException error) { _error = error.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        if (_module != null)
        {
            try { await _module.InvokeVoidAsync("uninstallAgentWorkbench", _ownerId); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose(); _lifetime.Dispose();
    }
    public sealed class WorkbenchState { public string[] Providers { get; set; } = []; public TaskView[] Tasks { get; set; } = []; public PendingView[] Pending { get; set; } = []; }
    public sealed class TaskView
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Status { get; set; } = ""; public string? StatusReason { get; set; }
        public string Draft { get; set; } = ""; public long TotalTokens { get; set; } public int CheckpointCount { get; set; }
        public string[] QueuedMessages { get; set; } = []; public PlanView[] Plan { get; set; } = []; public EventView[] Events { get; set; } = []; public ChangeView? Changes { get; set; }
    }
    public sealed class EventView { public long Sequence { get; set; } public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public string Text { get; set; } = ""; }
    public sealed class PlanView { public string Id { get; set; } = ""; public string Text { get; set; } = ""; public string Status { get; set; } = ""; }
    public sealed class PendingView { public string Id { get; set; } = ""; public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public JsonElement Content { get; set; } }
    public sealed class ChangeView { public long Revision { get; set; } public FileChange[] Files { get; set; } = []; }
    public sealed class FileChange { public string Path { get; set; } = ""; public string? Before { get; set; } public string? After { get; set; } public int? BeforeLength { get; set; } public int? AfterLength { get; set; } }
}
