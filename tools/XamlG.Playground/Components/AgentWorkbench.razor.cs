using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly string[] Profiles = ["ask", "readOnly", "plan", "autoEdit", "fullAccess", "custom"];
    private static readonly string[] Scopes = ["project", "source", "designer", "compiler", "runtime", "layout", "build", "agent"];
    private static readonly Dictionary<string, QueueEditor> _queueEditors = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private IJSObjectReference? _module;
    private DotNetObjectReference<AgentWorkbench>? _reference;
    private WorkbenchState _state = new();
    private string _provider = "", _model = "", _name = "New task", _taskName = "", _selectedId = "", _answer = "", _renderedLiveText = "";
    private readonly System.Text.StringBuilder _liveBuffer = new();
    private string _liveText { get => _renderedLiveText; set { _renderedLiveText = value; _liveBuffer.Clear().Append(value); } }
    private bool _streamRenderPending;
    private async Task RenderStreamAsync()
    {
        if (_streamRenderPending) return;
        _streamRenderPending = true;
        try
        {
            await Task.Delay(33, _lifetime.Token);
            await InvokeAsync(() => { _renderedLiveText = _liveBuffer.ToString(); if (!_disposed) StateHasChanged(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _streamRenderPending = false; }
    }
    private static readonly Dictionary<string, string> ComposerDrafts = new(StringComparer.Ordinal);
    private string _lastSelectedTask { get => LastSelectedTasks.GetValueOrDefault(BackendId, ""); set => LastSelectedTasks[BackendId] = value; }
    private string _draft { get => ComposerDrafts.GetValueOrDefault(_selectedId, Selected?.Draft ?? ""); set => ComposerDrafts[_selectedId] = value; }
    private ElementReference _composerElement;
    private bool CanRun => Selected is { IsPreviousWorkspace: false } task && !AnyRunning &&
        task.Status is "ready" or "completed" && ProviderReady && !_modelsBusy && !string.IsNullOrWhiteSpace(_draft) && _runReview == null;
    private ModelChoiceView[] _models = [];
    private string? _error;
    private RunReview? _runReview;
    private ElementReference _runReviewCancel;
    private bool _connected, _refreshing, _disposed, _fullAccessAcknowledged, _reviewBusy, _focusRunReview, _queueBusy, _liveTextTruncated;
    private Task? _refreshTask;
    private int _connectionVersion;
    private TaskView? Selected => _state.Tasks.FirstOrDefault(task => task.Id == _selectedId);
    private static bool IsRunning(TaskView task) => task.Status is "preparing" or "running" or "awaitingApproval" or "awaitingAnswer";
    private bool AnyRunning => _state.Tasks.Any(IsRunning);
    private QueueEditor QueueEdit => _queueEditors.TryGetValue(_selectedId, out var value) ? value : _queueEditors[_selectedId] = new();
    private int QueueIndex => Array.FindIndex(Selected?.Queue.Messages ?? [], message => message.Id == QueueEdit.Id);
    private ChangeView? SelectedChanges => _latestRun ? Selected?.LatestRunChanges : Selected?.Changes;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) ScheduleUiSave();
        if (_module != null && _renderedSection != _section)
        {
            _renderedSection = _section;
            await _module.InvokeVoidAsync("resetAgentPanelScroll", _contentElement, _section);
        }
        if (_focusRunReview) { _focusRunReview = false; await _runReviewCancel.FocusAsync(); }
        if (_module != null && _connected && Selected != null)
        {
            await _module.InvokeVoidAsync("bindAgentThread", _threadElement, Selected.Id, _ownerId);
            await _module.InvokeVoidAsync("bindAgentComposer", _composerElement, Selected.Id, _reference, _ownerId);
            if (Review.Diff != null) await _module.InvokeVoidAsync("bindAgentDiff", _diffElement, DiffScrollKey, _ownerId);
            else await _module.InvokeVoidAsync("releaseAgentViewKind", _ownerId, "diff");
        }
        else if (_module != null) await _module.InvokeVoidAsync("releaseAgentViews", _ownerId);
        if (!firstRender) return;
        _module = await JavaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "studio.js");
        await LoadNumericPreferencesAsync();
        _reference = DotNetObjectReference.Create(this);
        _connectionMode = _rememberedConnectionMode ?? (await _module.InvokeAsync<bool>("agentConnected") ? "companion" : "direct");
        await LoadUiStateAsync();
        if (BrowserRuntime != null) BrowserRuntime.Session.Harness.EventPublished += BrowserEventPublished;
        await _module.InvokeVoidAsync("installAgentWorkbench", _reference, _ownerId);
        await RefreshAsync();
        if (Selected != null) StateHasChanged();
        _ = PollAsync();
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
    [JSInvokable] public async Task AgentComposerSubmit(string taskId, string value, bool steer = false)
    {
        if (_disposed || !_connected || taskId != _selectedId || value.Length > 262144 || _runReview != null || _restoreReview != null || _handoff != null) return;
        _draft = value;
        await SendDraftAsync(steer); StateHasChanged();
    }
    [JSInvokable] public Task AgentComposerStop(string taskId) => taskId == _selectedId && Selected is { } task && IsRunning(task)
        ? CommandAsync("stop", new { }) : Task.CompletedTask;
    [JSInvokable] public void AgentDisconnected() { if (IsBrowser) return; _connected = false; _liveText = ""; _runReview = null; _restoreReview = null; _handoff = null; _fullAccessAcknowledged = false; _signInLaunchUrl = null; _signInLaunchId = null; StateHasChanged(); }
    [JSInvokable] public void AgentStreamError(string message) { if (IsBrowser) return; _error = message; StateHasChanged(); }
    [JSInvokable] public Task AgentStream(EventView item) => IsBrowser ? Task.CompletedTask : ProcessStreamAsync(item);
    private async Task ProcessStreamAsync(EventView item)
    {
        if (item.TaskId == _selectedId && item.Kind == "text_delta")
        {
            if (_liveBuffer.Length == 0) _liveTextTruncated = false;
            if (!_liveTextTruncated)
            {
                _liveBuffer.Append(item.Text);
                if (_liveBuffer.Length > 262144) { _liveTextTruncated = true; _liveBuffer.Length = 262000; _liveBuffer.Append("\n[streaming display shortened; use the retained transcript after the request finishes]"); }
                _ = RenderStreamAsync();
            }
        }
        else
        { if (item.TaskId == _selectedId && item.Kind is "request" or "retry" or "assistant" or "assistant_incomplete" or "completed" or "failed" or "paused" or "cancelled") _liveText = ""; await RefreshAsync(); }
    }
    private Task RefreshAsync() => _refreshTask is { IsCompleted: false } current ? current : _refreshTask = RefreshCoreAsync();
    private async Task RefreshAfterCommandAsync()
    {
        // A poll started before this command may not contain its result. Wait for
        // that snapshot, then request a fresh one before selecting a created task.
        if (_refreshTask is { IsCompleted: false } current) await current;
        await RefreshAsync();
    }
    private async Task RefreshCoreAsync()
    {
        if (_module == null || _disposed) return;
        _refreshing = true;
        var changed = true;
        var version = _connectionVersion;
        try
        {
            var connected = IsBrowser ? BrowserRuntime?.IsStateLoaded == true : await _module.InvokeAsync<bool>("agentConnected");
            if (_disposed || version != _connectionVersion) return;
            _connected = connected;
            if (_connected)
            {
                var next = await RequestAsync<WorkbenchState>("state", new { sessionId = _state.SessionId, revision = _state.Revision });
                if (_disposed || version != _connectionVersion) return;
                if (next.Unchanged) { changed = false; return; }
                foreach (var task in next.Tasks)
                    if (_state.Tasks.FirstOrDefault(previous => previous.Id == task.Id) is { } previous)
                    {
                        if (previous.Queue.Revision > task.Queue.Revision) task.Queue = previous.Queue;
                        if (previous.Changes?.ReviewVersion > (task.Changes?.ReviewVersion ?? -1)) task.Changes = previous.Changes;
                        if (previous.LatestRunChanges?.ReviewVersion > (task.LatestRunChanges?.ReviewVersion ?? -1)) task.LatestRunChanges = previous.LatestRunChanges;
                    }
                _state = next;
                foreach (var task in next.Tasks) TaskConnections[task.Id] = BackendId;
                UpdateAccountState(); PruneReviewState(); PruneThreadState();
                if (!_state.Providers.Contains(_provider)) { _provider = _state.Providers.Contains("openai") ? "openai" : _state.Providers.FirstOrDefault() ?? ""; ProviderChanged(); }
                if (Selected == null) Select(_state.Tasks.Any(task => task.Id == _lastSelectedTask) ? _lastSelectedTask : _state.Tasks.FirstOrDefault()?.Id ?? "");
                EnsureQueueSelection();
                if (_section == "Activity") await LoadActivityAsync();
                foreach (var id in ComposerDrafts.Keys.Where(RetiredView).ToArray()) ComposerDrafts.Remove(id);
                foreach (var id in _queueEditors.Keys.Where(RetiredView).ToArray()) _queueEditors.Remove(id);
                foreach (var id in _taskPreferences.Keys.Where(RetiredView).ToArray()) _taskPreferences.Remove(id);
            }
        }
        catch (JSException error) { if (version == _connectionVersion) _error = error.Message; }
        finally { _refreshing = false; if (!_disposed && changed) StateHasChanged(); }
    }
    private async Task CommandAsync(string action, object arguments)
    {
        try { _error = null; await RequestAsync<JsonElement>(action, arguments); await RefreshAfterCommandAsync(); }
        catch (Exception error) when (error is JSException or JsonException or ArgumentException) { _error = error.Message; }
    }
    private void Select(string id)
    {
        _selectedId = _lastSelectedTask = id; _taskName = Selected?.Name ?? ""; _liveText = ""; _runReview = null; _restoreReview = null; _fullAccessAcknowledged = false;
        if (IsBrowser && Selected is { } selected)
        {
            if (_provider != selected.ProviderId) { BrowserRuntime?.ClearCredentials(); _models = []; }
            _provider = selected.ProviderId; _model = selected.Model;
            SelectConnectionProfile();
        }
        EnsureQueueSelection();
    }
    private Task SelectTaskAsync(ChangeEventArgs args) { Select(args.Value?.ToString() ?? ""); return Task.CompletedTask; }
    private async Task DraftChangedAsync(ChangeEventArgs args)
    {
        _draft = args.Value?.ToString() ?? "";
        if (Selected != null)
            try { await RequestAsync<JsonElement>("draft", new { id = _selectedId, text = _draft, revision = NextDraftRevision(_selectedId) }); }
            catch (JSException error) { _error = error.Message; }
    }
    private async Task CreateAsync()
    {
        var section = _section;
        try
        {
            _error = null; var task = await RequestAsync<TaskView>("create", new { name = _name, provider = _provider, model = _model, accountId = _provider == ChatGptProvider ? ActiveAccount?.Id : null });
            await RefreshAfterCommandAsync(); Select(task.Id);
            if (_section == section) _section = "Conversation";
        }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task DiscoverModelsAsync()
    {
        if (_modelsBusy || AnyRunning) return;
        _modelsBusy = true;
        try
        {
            _error = null; var provider = _provider; var accountId = provider == ChatGptProvider ? ActiveAccount?.Id : null;
            var models = await RequestAsync<ModelChoiceView[]>("model_choices", new { provider, accountId });
            if (_provider == provider && (provider != ChatGptProvider || ActiveAccount?.Id == accountId)) _models = models;
        }
        catch (JSException error) { _error = error.Message; }
        finally { _modelsBusy = false; }
    }
    private object Options() => new
    {
        mode = Selected?.Mode ?? "default", continueQueuedMessages = Preferences.ContinueQueue,
        policy = new { profile = _profile, scopes = _scopes, tools = JsonSerializer.Deserialize<Dictionary<string, string>>(_toolRules), neverAsk = _neverAsk },
        limits = new { requestsPerRun = _requests, toolsPerRun = _tools, outputTokensPerRequest = _outputTokens, totalTaskTokens = _taskTokens,
            contextBytes = _contextBytes, toolResultBytes = _toolResultBytes, automaticRetries = _retries, requestTimeout = TimeSpan.FromMinutes(_timeoutMinutes) },
        leaseDuration = TimeSpan.FromMinutes(_leaseMinutes), automaticCompaction = _autoCompact, fullToolCatalog = Preferences.FullToolCatalog,
        compaction = new { automaticInputTokens = Preferences.Numeric.AutomaticInputTokens, modelContextWindowTokens = Preferences.Numeric.ModelContextWindowTokens,
            recentCompleteTurns = Preferences.Numeric.RecentCompleteTurns, checkpointOutputTokens = Preferences.Numeric.CheckpointOutputTokens }
    };
    private void ReviewRun(string? message, QueuedMessageView? queued = null, bool compact = false, long? planRevision = null)
    {
        if (Selected == null || Selected.IsPreviousWorkspace || AnyRunning) return;
        try
        {
            _error = null;
            var options = JsonSerializer.SerializeToElement(Options());
            if (planRevision != null)
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options)!;
                fields["mode"] = JsonSerializer.SerializeToElement("default"); options = JsonSerializer.SerializeToElement(fields);
            }
            _runReview = new(Selected.Id, Selected.Name, ProviderLabel(Selected.ProviderId) + (Selected.Account == null ? "" : " · " + Selected.Account.Label), Selected.Model, _profile, message,
                queued?.Id, queued == null ? null : Selected.Queue.Revision, compact ? "Generate a paid, tool-free public checkpoint. Keep the original goal, latest request and complete recent native turns. This does not send the composer draft or run IDE operations." : planRevision != null ? Selected.ProposedPlan?.Markdown : queued?.Text ?? message, options,
                $"{_requests:N0} requests and {_tools:N0} tool calls per run; {_outputTokens:N0} output tokens per request; {_taskTokens:N0} cumulative task tokens. " +
                (Selected.Account == null ? "" : "ChatGPT plan usage: output allowance is a local estimate, not a server cap. A response can exceed the remaining token budget. ") +
                $"{_contextBytes:N0} context bytes, {_toolResultBytes:N0} bytes per tool result, {_retries} automatic retries, {_timeoutMinutes}-minute requests and a {_leaseMinutes}-minute permission lease.", compact, planRevision);
            _fullAccessAcknowledged = false; _focusRunReview = true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { _error = error.Message; }
    }
    private void RunAsync() { if (!CanRun) return; if (_draft.Trim() == "/compact") ReviewRun(null, compact: true); else ReviewRun(_draft); }
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
            else if (review.Message != null)
                await SubmitTextAsync(review.Id, review.Message, review.Options, fullAccessAcknowledged: _fullAccessAcknowledged);
            else await RequestAsync<JsonElement>("run", new { id = review.Id, message = review.Message, options = review.Options,
                confirmed = true, fullAccessAcknowledged = _fullAccessAcknowledged, queuedMessageId = review.QueuedId, expectedQueueRevision = review.QueueRevision, planRevision = review.PlanRevision });
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
        if (_disposed) return;
        try { await ClosePanelAsync(); } catch (JSException) { }
        _disposed = true; _lifetime.Cancel();
        if (BrowserRuntime != null) { BrowserRuntime.Session.Harness.EventPublished -= BrowserEventPublished; ClearDirectCredentials(); }
        if (_module != null)
        {
            try { await _module.InvokeVoidAsync("releaseAgentThread", _threadElement); await _module.InvokeVoidAsync("releaseAgentComposer", _composerElement); await _module.InvokeVoidAsync("releaseAgentDiff", _diffElement); await _module.InvokeVoidAsync("uninstallAgentWorkbench", _ownerId); await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _reference?.Dispose(); _lifetime.Dispose();
    }
    public sealed class WorkbenchState { public string? SessionId { get; set; } public long Revision { get; set; } public bool Unchanged { get; set; } public int ToolCount { get; set; } public ConstraintView Constraints { get; set; } = new(); public ActivePermissionView? ActivePermissions { get; set; } public string[] Providers { get; set; } = []; public TaskView[] Tasks { get; set; } = []; public PendingView[] Pending { get; set; } = []; public OperationView[]? Operations { get; set; } = []; public AccountStateView? ChatGpt { get; set; } public string? ChatGptError { get; set; } }
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
        public string Mode { get; set; } = "default"; public ProposedPlanView? ProposedPlan { get; set; } public GoalView? ActiveGoal { get; set; }
        public AccountBindingView? Account { get; set; }
        public string Draft { get; set; } = ""; public long DraftRevision { get; set; } public int PublicEventCount { get; set; } public long TotalTokens { get; set; } public int CheckpointCount { get; set; }
        public bool IsPreviousWorkspace { get; set; }
        public ChangeView? LatestRunChanges { get; set; }
        public long ReportedTokens { get; set; } public long EstimatedTokens { get; set; } public int NativeContextBytes { get; set; }
        public DateTimeOffset? RetryAfterUtc { get; set; } public int? OutputLimitToExceed { get; set; }
        public QueueView Queue { get; set; } = new(); public PlanView[] Plan { get; set; } = []; public EventView[] Events { get; set; } = []; public ChangeView? Changes { get; set; }
    }
    public sealed class EventView
    {
        public long Sequence { get; set; }
        public string TaskId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
        public JsonElement? ResultPreview { get; set; }
        public XamlG.Automation.AutomationImage[]? Images { get; set; }
    }
    public sealed class PlanView { public string Id { get; set; } = ""; public string Text { get; set; } = ""; public string Status { get; set; } = ""; }
    public sealed class PendingView { public string Id { get; set; } = ""; public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public JsonElement Content { get; set; } }
    public sealed class ChangeView { public string ReviewId { get; set; } = ""; public long ReviewVersion { get; set; } public long Revision { get; set; } public FileChange[] Files { get; set; } = []; }
    public sealed class FileChange { public string Path { get; set; } = ""; public string ContentId { get; set; } = ""; public string? Before { get; set; } public string? After { get; set; } public int? BeforeLength { get; set; } public int? AfterLength { get; set; } }
    public sealed class QueueView { public long Revision { get; set; } public QueuedMessageView[] Messages { get; set; } = []; }
    public sealed class QueuedMessageView { public string Id { get; set; } = ""; public string Text { get; set; } = ""; public string Delivery { get; set; } = "queue"; }
    private sealed class QueueEditor { public string Id = "", Text = ""; public long Revision; }
    private sealed record RunReview(string Id, string Name, string Provider, string Model, string Profile, string? Message,
        string? QueuedId, long? QueueRevision, string? Preview, JsonElement Options, string Limits, bool Compact = false, long? PlanRevision = null);
}
