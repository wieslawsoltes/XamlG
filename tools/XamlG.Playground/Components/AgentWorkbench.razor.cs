using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly string[] Profiles = ["ask", "readOnly", "plan", "autoEdit", "fullAccess", "custom"];
    private static readonly string[] Scopes = ["project", "source", "designer", "compiler", "runtime", "layout", "build", "agent"];
    private readonly HashSet<string> _restorePaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueueEditor> _queueEditors = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private IJSObjectReference? _module;
    private DotNetObjectReference<AgentWorkbench>? _reference;
    private WorkbenchState _state = new();
    private string _provider = "", _model = "", _name = "New task", _taskName = "", _selectedId = "", _draft = "", _answer = "", _liveText = "";
    private ModelChoiceView[] _models = [];
    private FileChange[] _changePreview = [];
    private string? _error;
    private RunReview? _runReview;
    private ElementReference _runReviewCancel;
    private bool _connected, _refreshing, _disposed, _fullAccessAcknowledged, _reviewBusy, _focusRunReview, _queueBusy, _latestRun;
    private TaskView? Selected => _state.Tasks.FirstOrDefault(task => task.Id == _selectedId);
    private static bool IsRunning(TaskView task) => task.Status is "preparing" or "running" or "awaitingApproval" or "awaitingAnswer";
    private bool AnyRunning => _state.Tasks.Any(IsRunning);
    private QueueEditor QueueEdit => _queueEditors.TryGetValue(_selectedId, out var value) ? value : _queueEditors[_selectedId] = new();
    private int QueueIndex => Array.FindIndex(Selected?.Queue.Messages ?? [], message => message.Id == QueueEdit.Id);
    private ChangeView? SelectedChanges => _latestRun ? Selected?.LatestRunChanges : Selected?.Changes;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusRunReview) { _focusRunReview = false; await _runReviewCancel.FocusAsync(); }
        if (_module != null && _connected && Selected != null) await _module.InvokeVoidAsync("bindAgentThread", _threadElement, Selected.Id);
        if (!firstRender) return;
        _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
        await LoadNumericPreferencesAsync();
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
    [JSInvokable] public void AgentDisconnected() { _connected = false; _liveText = ""; _runReview = null; _fullAccessAcknowledged = false; _signInLaunchUrl = null; _signInLaunchId = null; StateHasChanged(); }
    [JSInvokable] public void AgentStreamError(string message) { _error = message; StateHasChanged(); }
    [JSInvokable] public async Task AgentStream(EventView item)
    {
        if (item.TaskId == _selectedId && item.Kind == "text_delta")
        { _liveText += item.Text; if (_liveText.Length > 262144) _liveText = _liveText[^262144..]; StateHasChanged(); }
        else
        { if (item.TaskId == _selectedId && item.Kind is "request" or "retry" or "assistant" or "completed" or "failed" or "paused") _liveText = ""; await RefreshAsync(); }
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
                var next = await RequestAsync<WorkbenchState>("state", new { });
                foreach (var task in next.Tasks)
                    if (_state.Tasks.FirstOrDefault(previous => previous.Id == task.Id) is { } previous && previous.Queue.Revision > task.Queue.Revision)
                        task.Queue = previous.Queue;
                _state = next;
                UpdateAccountState();
                if (!_state.Providers.Contains(_provider)) { _provider = _state.Providers.FirstOrDefault() ?? ""; ProviderChanged(); }
                if (Selected == null) Select(_state.Tasks.FirstOrDefault()?.Id ?? "");
                EnsureQueueSelection();
                foreach (var id in _queueEditors.Keys.Where(id => !_state.Tasks.Any(task => task.Id == id)).ToArray()) _queueEditors.Remove(id);
                foreach (var id in _taskPreferences.Keys.Where(id => !_state.Tasks.Any(task => task.Id == id)).ToArray()) _taskPreferences.Remove(id);
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
    { _selectedId = id; _draft = Selected?.Draft ?? ""; _taskName = Selected?.Name ?? ""; _liveText = ""; _changePreview = []; _diffPreview = []; _feedbackTarget = null; _restorePaths.Clear(); EnsureQueueSelection(); }
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
            _error = null; var task = await RequestAsync<TaskView>("create", new { name = _name, provider = _provider, model = _model, accountId = _provider == ChatGptProvider ? ActiveAccount?.Id : null });
            await RefreshAsync(); Select(task.Id);
        }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task DiscoverModelsAsync()
    {
        try
        {
            _error = null; var provider = _provider; var accountId = provider == ChatGptProvider ? ActiveAccount?.Id : null;
            var models = await RequestAsync<ModelChoiceView[]>("model_choices", new { provider, accountId });
            if (_provider == provider && (provider != ChatGptProvider || ActiveAccount?.Id == accountId)) _models = models;
        }
        catch (JSException error) { _error = error.Message; }
    }
    private object Options() => new
    {
        policy = new { profile = _profile, scopes = _scopes, tools = JsonSerializer.Deserialize<Dictionary<string, string>>(_toolRules), neverAsk = _neverAsk },
        limits = new { requestsPerRun = _requests, toolsPerRun = _tools, outputTokensPerRequest = _outputTokens, totalTaskTokens = _taskTokens,
            contextBytes = _contextBytes, toolResultBytes = _toolResultBytes, automaticRetries = _retries, requestTimeout = TimeSpan.FromMinutes(_timeoutMinutes) },
        leaseDuration = TimeSpan.FromMinutes(_leaseMinutes), automaticCompaction = _autoCompact,
        compaction = new { automaticInputTokens = Preferences.Numeric.AutomaticInputTokens, modelContextWindowTokens = Preferences.Numeric.ModelContextWindowTokens,
            recentCompleteTurns = Preferences.Numeric.RecentCompleteTurns, checkpointOutputTokens = Preferences.Numeric.CheckpointOutputTokens }
    };
    private void ReviewRun(string? message, QueuedMessageView? queued = null, bool compact = false)
    {
        if (Selected == null || Selected.IsPreviousWorkspace || AnyRunning) return;
        try
        {
            _error = null;
            var options = JsonSerializer.SerializeToElement(Options());
            _runReview = new(Selected.Id, Selected.Name, ProviderLabel(Selected.ProviderId) + (Selected.Account == null ? "" : " · " + Selected.Account.Label), Selected.Model, _profile, message,
                queued?.Id, queued == null ? null : Selected.Queue.Revision, compact ? "Generate a paid, tool-free public checkpoint. Keep the original goal, latest request and complete recent native turns. This does not send the composer draft or run IDE operations." : queued?.Text ?? message, options,
                $"{_requests:N0} requests and {_tools:N0} tool calls per run; {_outputTokens:N0} output tokens per request; {_taskTokens:N0} cumulative task tokens. " +
                (Selected.Account == null ? "" : "ChatGPT plan usage: output allowance is a local estimate, not a server cap. A response can exceed the remaining token budget. ") +
                $"{_contextBytes:N0} context bytes, {_toolResultBytes:N0} bytes per tool result, {_retries} automatic retries, {_timeoutMinutes}-minute requests and a {_leaseMinutes}-minute permission lease.", compact);
            _fullAccessAcknowledged = false; _focusRunReview = true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { _error = error.Message; }
    }
    private void RunAsync() { if (_draft.Trim() == "/compact") ReviewRun(null, compact: true); else ReviewRun(_draft); }
    private void ResumeAsync() => ReviewRun(null);
    private void ReviewCompaction() => ReviewRun(null, compact: true);
    private void ReviewQueuedRun()
    { if (Selected?.Queue.Messages.FirstOrDefault(message => message.Id == QueueEdit.Id) is { } queued) ReviewRun(null, queued); }
    private void CancelRunReview() { if (_reviewBusy) return; _runReview = null; _fullAccessAcknowledged = false; }
    private async Task ConfirmRunAsync()
    {
        if (_runReview is not { } review || _reviewBusy || (review.Profile == "fullAccess" && !_fullAccessAcknowledged)) return;
        _reviewBusy = true;
        try
        {
            _error = null;
            if (review.Compact) await RequestAsync<JsonElement>("compact", new { id = review.Id, options = review.Options, confirmed = true });
            else await RequestAsync<JsonElement>("run", new { id = review.Id, message = review.Message, options = review.Options,
                confirmed = true, fullAccessAcknowledged = _fullAccessAcknowledged, queuedMessageId = review.QueuedId, expectedQueueRevision = review.QueueRevision });
            _runReview = null; _fullAccessAcknowledged = false;
            await RefreshAsync();
        }
        catch (JSException error) { _error = error.Message; }
        finally { _reviewBusy = false; }
    }
    private void EnsureQueueSelection()
    {
        if (Selected == null) return;
        if (!Selected.Queue.Messages.Any(message => message.Id == QueueEdit.Id)) SelectQueue(Selected.Queue.Messages.FirstOrDefault()?.Id ?? "");
    }
    private void SelectQueue(string id)
    {
        QueueEdit.Id = id; QueueEdit.Text = Selected?.Queue.Messages.FirstOrDefault(message => message.Id == id)?.Text ?? "";
        QueueEdit.Revision = Selected?.Queue.Revision ?? 0;
    }
    private async Task<bool> ChangeQueueAsync(string action, object arguments)
    {
        var selected = Selected;
        if (selected == null || _queueBusy) return false;
        var editor = QueueEdit; var editedId = editor.Id; var editedText = editor.Text;
        var oldText = selected.Queue.Messages.FirstOrDefault(message => message.Id == editedId)?.Text;
        var oldRevision = selected.Queue.Revision; _queueBusy = true;
        try
        {
            _error = null; var queue = await RequestAsync<QueueView>(action, arguments);
            if (_state.Tasks.FirstOrDefault(task => task.Id == selected.Id) is { } current && current.Queue.Revision <= queue.Revision) current.Queue = queue;
            if (_selectedId == selected.Id)
            {
                EnsureQueueSelection();
                var savedText = Selected?.Queue.Messages.FirstOrDefault(message => message.Id == editedId)?.Text;
                // Typing can continue while the queue operation is in flight. Advance the
                // editor's base only when its saved text is unchanged or this save succeeded.
                if (QueueEdit.Id == editedId && QueueEdit.Revision == oldRevision &&
                    (savedText == oldText || (action == "queue_edit" && savedText == editedText))) QueueEdit.Revision = Selected!.Queue.Revision;
            }
            return true;
        }
        catch (JSException error) { _error = error.Message; return false; }
        finally { _queueBusy = false; }
    }
    private async Task QueueDraftAsync()
    {
        var text = _draft; var id = _selectedId;
        if (await ChangeQueueAsync("queue", new { id, text }) && _selectedId == id && _draft == text) _draft = "";
    }
    private Task SaveQueueAsync() => ChangeQueueAsync("queue_edit", new { id = _selectedId, messageId = QueueEdit.Id, text = QueueEdit.Text, expectedRevision = QueueEdit.Revision });
    private Task MoveQueueAsync(int delta) => ChangeQueueAsync("queue_move", new { id = _selectedId, messageId = QueueEdit.Id, index = QueueIndex + delta, expectedRevision = Selected!.Queue.Revision });
    private Task RemoveQueueAsync() => ChangeQueueAsync("queue_remove", new { id = _selectedId, messageId = QueueEdit.Id, expectedRevision = Selected!.Queue.Revision });
    private Task ClearQueueAsync() => ChangeQueueAsync("clear_queue", new { id = _selectedId, expectedRevision = Selected!.Queue.Revision });
    private Task RespondAsync(string id, string value) => CommandAsync("respond", new { id, value });
    private void ChangeScope(string scope, ChangeEventArgs args)
    { var value = args.Value?.ToString() ?? "default"; if (value == "default") _scopes.Remove(scope); else _scopes[scope] = value; }
    private void SelectRestore(string path, ChangeEventArgs args)
    { if (args.Value is true) _restorePaths.Add(path); else _restorePaths.Remove(path); }
    private async Task PreviewChangesAsync()
    { try { _changePreview = (await RequestAsync<ChangeView>("changes", new { id = _selectedId, latestRun = _latestRun })).Files; _diffPreview = []; } catch (JSException error) { _error = error.Message; } }
    private void ChangeBaseline(ChangeEventArgs args)
    { _latestRun = args.Value?.ToString() == "latest"; _changePreview = []; _diffPreview = []; _restorePaths.Clear(); _feedbackTarget = null; }
    private Task RestoreAsync() => CommandAsync("restore", new { id = _selectedId, paths = _restorePaths.ToArray(), expectedRevision = SelectedChanges!.Revision, latestRun = _latestRun });
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
            try { await _module.InvokeVoidAsync("releaseAgentThread", _threadElement); await _module.InvokeVoidAsync("uninstallAgentWorkbench", _ownerId); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose(); _lifetime.Dispose();
    }
    public sealed class WorkbenchState { public string[] Providers { get; set; } = []; public TaskView[] Tasks { get; set; } = []; public PendingView[] Pending { get; set; } = []; public OperationView[]? Operations { get; set; } = []; public AccountStateView? ChatGpt { get; set; } public string? ChatGptError { get; set; } }
    public sealed class OperationView
    {
        public string TaskId { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }
    }
    public sealed class TaskView
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Status { get; set; } = ""; public string? StatusReason { get; set; }
        public string ProviderId { get; set; } = ""; public string Model { get; set; } = "";
        public AccountBindingView? Account { get; set; }
        public string Draft { get; set; } = ""; public long TotalTokens { get; set; } public int CheckpointCount { get; set; }
        public bool IsPreviousWorkspace { get; set; }
        public ChangeView? LatestRunChanges { get; set; }
        public long ReportedTokens { get; set; } public long EstimatedTokens { get; set; } public int NativeContextBytes { get; set; }
        public DateTimeOffset? RetryAfterUtc { get; set; } public int? OutputLimitToExceed { get; set; }
        public QueueView Queue { get; set; } = new(); public PlanView[] Plan { get; set; } = []; public EventView[] Events { get; set; } = []; public ChangeView? Changes { get; set; }
    }
    public sealed class EventView { public long Sequence { get; set; } public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public string Text { get; set; } = ""; }
    public sealed class PlanView { public string Id { get; set; } = ""; public string Text { get; set; } = ""; public string Status { get; set; } = ""; }
    public sealed class PendingView { public string Id { get; set; } = ""; public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public JsonElement Content { get; set; } }
    public sealed class ChangeView { public long Revision { get; set; } public FileChange[] Files { get; set; } = []; }
    public sealed class FileChange { public string Path { get; set; } = ""; public string? Before { get; set; } public string? After { get; set; } public int? BeforeLength { get; set; } public int? AfterLength { get; set; } }
    public sealed class QueueView { public long Revision { get; set; } public QueuedMessageView[] Messages { get; set; } = []; }
    public sealed class QueuedMessageView { public string Id { get; set; } = ""; public string Text { get; set; } = ""; }
    private sealed class QueueEditor { public string Id = "", Text = ""; public long Revision; }
    private sealed record RunReview(string Id, string Name, string Provider, string Model, string Profile, string? Message,
        string? QueuedId, long? QueueRevision, string? Preview, JsonElement Options, string Limits, bool Compact = false);
}
