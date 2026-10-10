using System.Collections.Concurrent;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

/// <summary>Reusable task, approval, queue and review orchestration for browser and companion hosts.</summary>
public class AgentWorkbenchSession : IDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly AgentHarness _harness;
    private readonly Func<string?>? _workspaceIdentity;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _run;
    private CancellationTokenSource? _runCancellation;
    private string? _runningId;
    private readonly Queue<AgentEventDisplay> _activity = new();
    private int _activityBytes;
    private bool _disposed;
    private long _stateRevision = 1, _cachedRevision;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private string? _externalState;
    private JsonElement _cachedState;
    private readonly object _stateGate = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AgentEvent, AgentEventDisplay> _eventViews = new();
    private DateTimeOffset? _cachedPermissionExpiry;
    private AgentEventDisplay DisplayEvent(AgentEvent item) => _eventViews.GetValue(item, value => AgentEventDisplay.Create(value, 8192));
    private static readonly HashSet<string> ReadActions = new(StringComparer.Ordinal)
    { "state", "tools", "activity", "thread", "models", "model_choices", "pending_export", "export", "export_markdown", "handoff", "diff_file", "change_file", "block_preview", "diff", "patch", "changes" };
    public AgentWorkbenchSession(IAutomationHost host, IEnumerable<IAgentProvider> providers, IAgentWorkspace? workspace = null, AgentPermissionConstraints? constraints = null, Func<string?>? workspaceIdentity = null)
    { _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal); _harness = new(host, workspace, constraints); _harness.EventPublished += RecordActivity; _workspaceIdentity = workspaceIdentity; }
    protected virtual IEnumerable<string> ProviderIds => _providers.Keys;
    protected virtual object? AccountState => null;
    protected virtual string? AccountError => null;
    protected virtual object? OperationState => null;
    protected virtual object? DescribeAccount(IAgentProvider provider) => null;
    protected virtual async Task<object> ModelChoicesAsync(ProviderArgs args, CancellationToken cancellationToken) =>
        (await Provider(args.Provider, args.AccountId).ListModelsAsync(cancellationToken)).Select(model => new { slug = model, displayName = model }).ToArray();
    protected virtual ValueTask<JsonElement> ExecuteExtensionAsync(string action, JsonElement arguments, CancellationToken cancellationToken, CancellationToken ownerSession) =>
        throw new ArgumentException("Unknown agent action.");
    public AgentHarness Harness => _harness;
    public bool IsRunning { get { lock (_gate) return _run is { IsCompleted: false } || _harness.ActivePermissions != null; } }

    public void RestoreSession(AgentSessionSnapshot snapshot, CancellationToken workspaceLifetime = default)
    {
        _harness.RestoreSession(snapshot, (id, account) =>
        {
            try { return Provider(id, account); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
            { return new UnavailableProvider(id, account); }
        }, workspaceLifetime, _workspaceIdentity?.Invoke());
        foreach (var item in _harness.Tasks.SelectMany(task => task.Events).OrderBy(item => item.Sequence).TakeLast(500)) RecordActivity(item);
        Interlocked.Increment(ref _stateRevision);
    }

    public async Task<JsonElement> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken, CancellationToken ownerSession = default)
    {
        if (_workspaceIdentity?.Invoke() is { Length: > 0 } identity && !ownerSession.IsCancellationRequested && _harness.ReconnectWorkspace(identity, ownerSession))
        { Interlocked.Increment(ref _stateRevision); await _harness.SaveSessionAsync(cancellationToken); }
        try { return await ExecuteCoreAsync(action, arguments, cancellationToken, ownerSession); }
        finally
        {
            if (!ReadActions.Contains(action))
            {
                Interlocked.Increment(ref _stateRevision);
                if (action is not ("run" or "stop" or "respond")) await _harness.SaveSessionAsync(cancellationToken);
            }
        }
    }

    private async Task<JsonElement> ExecuteCoreAsync(string action, JsonElement arguments, CancellationToken cancellationToken, CancellationToken ownerSession)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (action)
        {
            case "state":
                lock (_stateGate)
                {
                var accounts = AccountState; var accountError = AccountError; var operations = OperationState;
                var external = JsonSerializer.Serialize(new { accounts, accountError, operations, toolCount = _harness.ToolCatalog.Count,
                    retired = _harness.Tasks.OrderBy(task => task.Id, StringComparer.Ordinal).Select(task => new { task.Id, task.IsPreviousWorkspace }) }, AutomationJson.Options);
                if (_externalState != external) { _externalState = external; Interlocked.Increment(ref _stateRevision); }
                if (_cachedPermissionExpiry <= DateTimeOffset.UtcNow) { _cachedPermissionExpiry = null; Interlocked.Increment(ref _stateRevision); }
                var stateRevision = Volatile.Read(ref _stateRevision);
                if (arguments.TryGetProperty("sessionId", out var sessionId) && sessionId.ValueKind == JsonValueKind.String && sessionId.GetString() == _sessionId &&
                    arguments.TryGetProperty("revision", out var known) && known.ValueKind == JsonValueKind.Number && known.GetInt64() == stateRevision)
                    return AutomationJson.Element(new { sessionId = _sessionId, revision = stateRevision, unchanged = true });
                if (_cachedRevision == stateRevision) return _cachedState;
                _cachedPermissionExpiry = _harness.ActivePermissions?.ExpiresAt;
                _cachedState = AutomationJson.Element(new
                {
                    sessionId = _sessionId, revision = stateRevision,
                    providers = ProviderIds.Order(StringComparer.Ordinal), toolCount = _harness.ToolCatalog.Count, constraints = _harness.Constraints, activePermissions = _harness.ActivePermissions,
                    chatGpt = accounts, chatGptError = accountError, tasks = _harness.Tasks.Select(task => new
                    {
                        task.Id, task.Name, task.ProviderId, task.Model, task.Status, task.StatusReason, task.Draft, task.DraftRevision, task.IsPreviousWorkspace,
                        task.Mode, task.ProposedPlan, task.ActiveGoal,
                        account = DescribeAccount(task.Provider),
                        task.TotalTokens, task.ReportedTokens, task.EstimatedTokens, task.CheckpointCount, task.Plan, task.PlanRevision, task.Queue,
                        task.LastUsage, task.NativeContextBytes, task.RetryAfterUtc, task.OutputLimitToExceed,
                        changes = task.Changes == null ? null : new { task.Changes.ReviewId, task.Changes.ReviewVersion, task.Changes.Revision, files = task.Changes.Files.Select(file => new { file.Path, contentId = task.Changes.FileIdentities[file.Path], beforeLength = file.Before?.Length, afterLength = file.After?.Length }) },
                        latestRunChanges = task.LatestRunChanges == null ? null : new { task.LatestRunChanges.ReviewId, task.LatestRunChanges.ReviewVersion, task.LatestRunChanges.Revision, files = task.LatestRunChanges.Files.Select(file => new { file.Path, contentId = task.LatestRunChanges.FileIdentities[file.Path], beforeLength = file.Before?.Length, afterLength = file.After?.Length }) },
                        publicEventCount = task.Events.Count(item => item.Kind != "text_delta"),
                        events = task.Events.Where(item => item.Kind != "text_delta").TakeLast(80).Select(DisplayEvent)
                    }),
                    pending = _pending.Values.Select(p => new { p.Id, p.TaskId, p.Kind, content = PublicPending(p) }),
                    operations
                });
                _cachedRevision = stateRevision;
                return _cachedState;
                }
            case "tools": return AutomationJson.Element(_harness.ToolCatalog);
            case "revoke_grant":
                var grant = Read<TextArgs>(arguments); return AutomationJson.Element(new { revoked = _harness.RevokeGrant(grant.Id, grant.Text) });
            case "activity": lock (_gate) return AutomationJson.Element(_activity.ToArray());
            case "activity_clear": lock (_gate) { _activity.Clear(); _activityBytes = 0; } break;
            case "thread":
                var thread = Read<ThreadArgs>(arguments);
                if (thread.MaximumEntries is < 1 or > 100 || thread.BeforeSequence is <= 0 || thread.AfterSequence is <= 0 || thread.BeforeSequence != null && thread.AfterSequence != null)
                    throw new ArgumentException("Select a bounded thread page in one direction.");
                var all = _harness.GetTask(thread.Id).Events.Where(item => item.Kind != "text_delta").ToArray();
                var candidates = all.Where(item => (thread.BeforeSequence == null || item.Sequence < thread.BeforeSequence) &&
                    (thread.AfterSequence == null || item.Sequence > thread.AfterSequence));
                if (thread.AfterSequence == null) candidates = candidates.Reverse();
                var page = new List<AgentEventDisplay>(); var characters = 0;
                foreach (var item in candidates.Take(thread.MaximumEntries))
                {
                    var text = item.Text.Length > 8192 ? item.Text[..8192] + "\n[see transcript export]" : item.Text;
                    if (characters + text.Length > 262144) break;
                    page.Add(DisplayEvent(item)); characters += text.Length;
                }
                if (thread.AfterSequence == null) page.Reverse();
                return AutomationJson.Element(new { events = page, hasEarlier = page.Count > 0 && all.Any(item => item.Sequence < page[0].Sequence),
                    hasLater = page.Count > 0 && all.Any(item => item.Sequence > page[^1].Sequence) });
            case "models":
                var modelRequest = Read<ProviderArgs>(arguments);
                return AutomationJson.Element(await Provider(modelRequest.Provider, modelRequest.AccountId).ListModelsAsync(cancellationToken));
            case "model_choices":
                return AutomationJson.Element(await ModelChoicesAsync(Read<ProviderArgs>(arguments), cancellationToken));
            case "create":
                var create = Read<CreateArgs>(arguments);
                return AutomationJson.Element(_harness.CreateTask(create.Name, Provider(create.Provider, create.AccountId), create.Model, ownerSession, _workspaceIdentity?.Invoke()));
            case "rename":
                var rename = Read<TextArgs>(arguments); _harness.RenameTask(rename.Id, rename.Text); break;
            case "draft":
                var draft = Read<DraftArgs>(arguments);
                if (draft.Text.Length > 262144) throw new ArgumentException("Draft is too large.");
                var draftTask = _harness.GetTask(draft.Id);
                lock (draftTask.Sync)
                    if (draft.Revision == null || draft.Revision > draftTask.DraftRevision)
                    { draftTask.Draft = draft.Text; draftTask.DraftRevision = draft.Revision ?? checked(draftTask.DraftRevision + 1); }
                break;
            case "delete":
                var deleted = _harness.GetTask(Read<IdArgs>(arguments).Id); _harness.DeleteTask(deleted.Id);
                if (_harness.Tasks.Count == 0) _harness.CreateTask("New task", deleted.Provider, deleted.Model, ownerSession, _workspaceIdentity?.Invoke());
                break;
            case "queue":
                var queue = Read<SubmitArgs>(arguments); _harness.QueueMessage(queue.Id, queue.Text, queue.Delivery, queue.ClientMessageId);
                var queuedTask = _harness.GetTask(queue.Id); if (queuedTask.Draft == queue.Text) queuedTask.Draft = "";
                return AutomationJson.Element(queuedTask.Queue);
            case "send":
                return await SubmitAsync(Read<SubmitArgs>(arguments), cancellationToken, ownerSession);
            case "mode":
                var mode = Read<ModeArgs>(arguments); _harness.SetMode(mode.Id, mode.Mode); break;
            case "goal_set":
                var goal = Read<GoalArgs>(arguments); _harness.SetGoal(goal.Id, goal.Objective ?? "", goal.TokenBudget); break;
            case "goal_pause":
                var pausedGoal = Read<IdArgs>(arguments); _harness.PauseGoal(pausedGoal.Id);
                lock (_gate) if (_runningId == pausedGoal.Id) Stop();
                break;
            case "goal_resume":
                var resumedGoal = Read<GoalArgs>(arguments); _harness.ResumeGoal(resumedGoal.Id, resumedGoal.TokenBudget); break;
            case "goal_clear":
                _harness.ClearGoal(Read<IdArgs>(arguments).Id); break;
            case "queue_edit":
                var edit = Read<QueueEditArgs>(arguments); _harness.EditQueuedMessage(edit.Id, edit.MessageId, edit.Text, edit.ExpectedRevision);
                return AutomationJson.Element(_harness.GetTask(edit.Id).Queue);
            case "queue_move":
                var move = Read<QueueMoveArgs>(arguments); _harness.MoveQueuedMessage(move.Id, move.MessageId, move.Index, move.ExpectedRevision);
                return AutomationJson.Element(_harness.GetTask(move.Id).Queue);
            case "queue_remove":
                var remove = Read<QueueMessageArgs>(arguments); _harness.RemoveQueuedMessage(remove.Id, remove.MessageId, remove.ExpectedRevision);
                return AutomationJson.Element(_harness.GetTask(remove.Id).Queue);
            case "clear_queue":
                var clear = Read<QueueArgs>(arguments); _harness.ClearQueuedMessages(clear.Id, clear.ExpectedRevision);
                return AutomationJson.Element(_harness.GetTask(clear.Id).Queue);
            case "compact":
                var compact = Read<CompactArgs>(arguments);
                if (!compact.Confirmed) throw new InvalidOperationException("Review the provider request and confirm compaction first.");
                using (var compactionLife = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerSession,
                    _harness.GetTask(compact.Id).Provider is IAgentProviderSession compactAccount ? compactAccount.GetSessionLifetime() : default))
                    return AutomationJson.Element(new { compacted = await _harness.CompactAsync(compact.Id, compact.Options, compactionLife.Token) });
            case "stop": Stop(); break;
            case "run":
                var run = Read<RunArgs>(arguments);
                run = run with { Options = _harness.ValidateOptions(run.Options with { FullAccessAcknowledged = run.FullAccessAcknowledged }) };
                var task = _harness.GetTask(run.Id);
                if (task.IsPreviousWorkspace) throw new InvalidOperationException("This task belongs to a previous workspace. Create a new task for the current project.");
                if (!run.Confirmed) throw new InvalidOperationException("Review and confirm this run first.");
                if (run.Options.Policy.Profile == PermissionProfile.FullAccess && !run.FullAccessAcknowledged)
                    throw new InvalidOperationException("A fresh Full Access acknowledgement is required for this run.");
                if (task.Status is AgentTaskStatus.Cancelled or AgentTaskStatus.Failed) throw new InvalidOperationException("Create a new task after cancellation or failure.");
                if (run.QueuedMessageId != null)
                {
                    if (run.Message != null || run.ExpectedQueueRevision == null || task.Status is not (AgentTaskStatus.Ready or AgentTaskStatus.Completed))
                        throw new InvalidOperationException("Send a queued message only to a new or completed task, with the reviewed queue revision.");
                    _harness.GetQueuedMessage(run.Id, run.QueuedMessageId, run.ExpectedQueueRevision.Value);
                }
                else if (run.PlanRevision == null && (run.Message == null) != (task.Status == AgentTaskStatus.Paused)) throw new InvalidOperationException("Resume a paused task without adding a new message.");
                if (run.PlanRevision != null && (run.Message != null || run.QueuedMessageId != null || task.ProposedPlan is not { Accepted: false } proposal || proposal.Revision != run.PlanRevision))
                    throw new InvalidOperationException("Select the current proposed plan to implement.");
                if (run.Message != null && (string.IsNullOrWhiteSpace(run.Message) || run.Message.Length > 262144)) throw new ArgumentException("A bounded message is required.");
                lock (_gate)
                {
                    if (_run is { IsCompleted: false }) throw new InvalidOperationException("An agent is already running.");
                    ownerSession.ThrowIfCancellationRequested();
                    var accountLife = task.Provider is IAgentProviderSession runAccount ? runAccount.GetSessionLifetime() : default;
                    StartRun(run, ownerSession, accountLife);
                }
                break;
            case "pending_export":
                var exportId = Read<IdArgs>(arguments).Id;
                if (!_pending.TryGetValue(exportId, out var exportPending)) throw new InvalidOperationException("This review is no longer pending.");
                return AutomationJson.Element(exportPending.Content.GetRawText());
            case "respond":
                var response = Read<ResponseArgs>(arguments);
                if (!_pending.TryGetValue(response.Id, out var pending)) throw new InvalidOperationException("This question or approval is no longer pending.");
                if (pending.Kind == "approval") _ = response.Value.Deserialize<AgentApproval>(AutomationJson.Options);
                else if (response.Value.ValueKind != JsonValueKind.String || response.Value.GetString()!.Length > 16384) throw new ArgumentException("A bounded text answer is required.");
                if (!pending.Completion.TrySetResult(response.Value.Clone())) throw new InvalidOperationException("Already answered.");
                break;
            case "export": return AutomationJson.Element(_harness.ExportTranscript(Read<IdArgs>(arguments).Id));
            case "export_markdown": return AutomationJson.Element(_harness.ExportMarkdown(Read<IdArgs>(arguments).Id));
            case "handoff": return AutomationJson.Element(_harness.CreateContextHandoff(Read<IdArgs>(arguments).Id));
            case "changes_refresh":
                var refresh = Read<ChangesArgs>(arguments);
                var refreshed = await _harness.RefreshChangesAsync(refresh.Id, refresh.LatestRun, cancellationToken);
                return AutomationJson.Element(new { refreshed.ReviewId, refreshed.ReviewVersion, refreshed.Revision,
                    files = refreshed.Files.Select(file => new { file.Path, contentId = refreshed.FileIdentities[file.Path], beforeLength = file.Before?.Length, afterLength = file.After?.Length }) });
            case "diff_file":
                var fileDiff = Read<FileReviewArgs>(arguments); var fileReview = Review(fileDiff);
                return AutomationJson.Element(new { fileReview.ReviewId, fileReview.Revision,
                    file = AgentSourceReview.Diff(ReviewFile(fileReview, fileDiff.Path), fileDiff.MaximumLines, fileDiff.FirstRow) });
            case "change_file":
                var filePreview = Read<FileReviewArgs>(arguments); var previewReview = Review(filePreview); var previewFile = ReviewFile(previewReview, filePreview.Path);
                return AutomationJson.Element(new { previewReview.ReviewId, previewReview.Revision, previewFile.Path,
                    before = Excerpt(previewFile.Before), after = Excerpt(previewFile.After) });
            case "block_preview":
                var blockPreview = Read<BlockReviewArgs>(arguments);
                var blockReview = _harness.GetChangeReview(blockPreview.Id, blockPreview.LatestRun, blockPreview.ReviewId);
                return AutomationJson.Element(AgentSourceReview.PreviewBlock(ReviewFile(blockReview, blockPreview.Path), blockPreview.BlockId));
            case "diff":
                var diff = Read<ChangesArgs>(arguments);
                return AutomationJson.Element(_harness.GetChangeReview(diff.Id, diff.LatestRun, diff.ReviewId).Files.Select(file => AgentSourceReview.Diff(file)).ToArray());
            case "patch":
                var patch = Read<ChangesArgs>(arguments);
                return AutomationJson.Element(AgentSourceReview.Patch(_harness.GetChangeReview(patch.Id, patch.LatestRun, patch.ReviewId)));
            case "changes":
                var changes = Read<ChangesArgs>(arguments);
                return AutomationJson.Element(_harness.GetChangeReview(changes.Id, changes.LatestRun, changes.ReviewId));
            case "restore":
                var restore = Read<RestoreArgs>(arguments);
                return AutomationJson.Element(await _harness.RestoreChangesAsync(restore.Id, restore.Paths, restore.ExpectedRevision, cancellationToken, restore.LatestRun, restore.ReviewId));
            case "restore_block":
                var block = Read<BlockRestoreArgs>(arguments);
                return AutomationJson.Element(await _harness.RestoreBlockAsync(block.Id, block.Path, block.BlockId, block.ReviewId,
                    block.ExpectedRevision, cancellationToken, block.LatestRun));
            default: return await ExecuteExtensionAsync(action, arguments, cancellationToken, ownerSession);
        }
        return AutomationJson.Element(new { accepted = true });
    }

    private AgentChangeReview Review(FileReviewArgs args) => _harness.GetChangeReview(args.Id, args.LatestRun, args.ReviewId);
    private static AgentFileChange ReviewFile(AgentChangeReview review, string path) => review.Files.SingleOrDefault(file => file.Path == path)
        ?? throw new ArgumentException("Select a document from this source comparison.");
    private static string? Excerpt(string? text) => text is { Length: > 20000 } ? text[..20000] + "\n[display excerpt; export the patch for complete source]" : text;

    private void StartRun(RunArgs args, CancellationToken ownerSession, CancellationToken accountLife)
    {
        _runCancellation?.Dispose(); _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ownerSession, accountLife);
        _runningId = args.Id;
        _run = RunAsync(args, _runCancellation.Token);
    }

    private async Task<JsonElement> SubmitAsync(SubmitArgs args, CancellationToken cancellationToken, CancellationToken ownerSession)
    {
        if (string.IsNullOrWhiteSpace(args.ClientMessageId)) throw new ArgumentException("Sending requires a stable client message ID so retries cannot duplicate a turn.");
        var task = _harness.GetTask(args.Id);
        if (task.IsPreviousWorkspace || task.Status is AgentTaskStatus.Cancelled or AgentTaskStatus.Failed)
            throw new InvalidOperationException("Choose an available thread before sending.");
        var options = _harness.ValidateOptions((args.Options ?? new() { Mode = task.Mode }) with { FullAccessAcknowledged = args.FullAccessAcknowledged }, requireAcknowledgement: false);
        Task? finishing = null;
        lock (_gate)
        {
            // A running thread keeps its current authority. Options are used only when this
            // explicit submission starts a new run, never to broaden an active lease.
            if (_run is not { IsCompleted: false }) options = _harness.ValidateOptions(options);
            _harness.QueueMessage(args.Id, args.Text, args.Delivery, args.ClientMessageId);
            lock (task.Sync)
            {
                if (args.DraftRevision is { } sentRevision && sentRevision > task.DraftRevision)
                { task.DraftRevision = sentRevision; task.Draft = ""; }
                else if (args.DraftRevision == null && task.Draft == args.Text) { task.Draft = ""; task.DraftRevision++; }
            }
            if (_runningId == args.Id && task.Status == AgentTaskStatus.Completed) finishing = _run;
        }
        // Acknowledgment means durable acceptance, not just that a provider request was scheduled.
        await _harness.SaveSessionAsync(cancellationToken);
        if (finishing != null) await finishing.WaitAsync(cancellationToken);
        lock (_gate)
        {
            var queue = task.Queue;
            if (_run is not { IsCompleted: false } && task.Status is AgentTaskStatus.Ready or AgentTaskStatus.Completed &&
                queue.Messages.FirstOrDefault(message => message.Id == args.ClientMessageId) is { } submitted)
            {
                options = _harness.ValidateOptions(options);
                ownerSession.ThrowIfCancellationRequested();
                var accountLife = task.Provider is IAgentProviderSession account ? account.GetSessionLifetime() : default;
                var next = options.ContinueQueuedMessages ? queue.Messages[0] : submitted;
                StartRun(new(args.Id, null, options, Confirmed: true, FullAccessAcknowledged: args.FullAccessAcknowledged,
                    QueuedMessageId: next.Id, ExpectedQueueRevision: queue.Revision), ownerSession, accountLife);
            }
        }
        return AutomationJson.Element(new { accepted = true, messageId = args.ClientMessageId, queue = task.Queue });
    }

    private async Task RunAsync(RunArgs args, CancellationToken cancellationToken)
    {
        // Begin after the caller has recorded ownership, and never tie a run to one HTTP poll.
        await Task.Yield();
        try
        {
            async Task<AgentApproval> Review(AutomationReview review, CancellationToken token) =>
                (await AskAsync("approval", AutomationJson.Element(review), token)).Deserialize<AgentApproval>(AutomationJson.Options);
            async Task<string> Question(AgentQuestion question, CancellationToken token) =>
                (await AskAsync("question", AutomationJson.Element(question), token)).GetString()!;
            if (args.PlanRevision is { } revision)
                await _harness.ImplementPlanAsync(args.Id, revision, args.Options, Review, Question, cancellationToken);
            else if (args.QueuedMessageId != null)
                await _harness.RunQueuedAsync(args.Id, args.QueuedMessageId, args.ExpectedQueueRevision!.Value, args.Options, Review, Question, cancellationToken);
            else await _harness.RunAsync(args.Id, args.Message, args.Options, Review, Question, cancellationToken);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { /* The harness retains the failure in task status and its public thread. */ }
        finally { lock (_gate) _runningId = null; Interlocked.Increment(ref _stateRevision); }
    }

    private async Task<JsonElement> AskAsync(string kind, JsonElement content, CancellationToken token)
    {
        var item = new Pending(Guid.NewGuid().ToString("N"), _runningId!, kind, content);
        _pending.TryAdd(item.Id, item);
        Interlocked.Increment(ref _stateRevision);
        try { return await item.Completion.Task.WaitAsync(token); }
        finally { _pending.TryRemove(item.Id, out _); Interlocked.Increment(ref _stateRevision); }
    }

    private static JsonElement PublicPending(Pending pending)
    {
        if (pending.Kind != "approval") return pending.Content;
        var review = pending.Content.Deserialize<AutomationReview>(AutomationJson.Options)!;
        var arguments = review.Arguments.GetRawText();
        object? preview = null;
        if (review.Preview is { ValueKind: JsonValueKind.Object } source)
        {
            var files = source.TryGetProperty("files", out var changes) ? changes.EnumerateArray().ToArray() : [];
            preview = new
            {
                revision = source.TryGetProperty("revision", out var revision) ? (long?)revision.GetInt64() : null,
                note = source.TryGetProperty("note", out var note) ? note.GetString() : null,
                truncated = files.Length > 20 || files.Any(file => new[] { "before", "after" }.Any(name => file.GetProperty(name).GetString()?.Length > 12000)),
                files = files.Take(20).Select(file => new { path = file.GetProperty("path").GetString(), before = PreviewText(file.GetProperty("before").GetString()), after = PreviewText(file.GetProperty("after").GetString()) })
            };
        }
        return AutomationJson.Element(new { tool = new { review.Tool.Name, review.Tool.Description, review.Tool.Scope, review.Tool.Effect, review.Tool.Destructive, review.Tool.AdditionalEffects },
            arguments = arguments.Length > 32768 ? AutomationJson.Element(new { excerpt = arguments[..32768], truncated = true }) : review.Arguments, review.Caller, preview });
        static string? PreviewText(string? text) => text?.Length > 12000 ? text[..12000] + "\n[display excerpt; save full review]" : text;
    }

    private void RecordActivity(AgentEvent item)
    {
        if (item.Kind == "text_delta") return;
        Interlocked.Increment(ref _stateRevision);
        var text = item.Kind is "user" or "assistant" or "answer" or "question" ? $"{item.Kind} message · {item.Text.Length:N0} characters" :
            item.Text.Length <= 2048 ? item.Text : item.Text[..2048] + " [excerpt]";
        lock (_gate)
        {
            var entry = AgentEventDisplay.Create(item, 2048) with { Text = text };
            _activity.Enqueue(entry); _activityBytes += ActivityBytes(entry);
            while (_activity.Count > 500 || _activityBytes > 524288) _activityBytes -= ActivityBytes(_activity.Dequeue());
        }
    }
    private static int ActivityBytes(AgentEventDisplay item) => JsonSerializer.SerializeToUtf8Bytes(item, AutomationJson.Options).Length + 1;

    protected virtual IAgentProvider Provider(string id, string? accountId = null) =>
        _providers.TryGetValue(id, out var provider) ? provider : throw new ArgumentException("Provider is not configured for this connection.");
    protected static T Read<T>(JsonElement arguments) => arguments.Deserialize<T>(AutomationJson.Options) ?? throw new ArgumentException("Arguments are required.");
    public void Stop() { lock (_gate) { if (_disposed) return; _harness.Stop(); _runCancellation?.Cancel(); } }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _harness.EventPublished -= RecordActivity;
            _harness.Dispose(); _runCancellation?.Dispose(); _lifetime.Dispose();
        }
    }
    private sealed record Pending(string Id, string TaskId, string Kind, JsonElement Content)
    { public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class UnavailableProvider(string id, string? account) : IAgentProvider, IAgentProviderState
    {
        public string Id => id;
        public string? AccountIdentity => account;
        public JsonElement SaveNative(object native) => ((JsonElement)native).Clone();
        public object RestoreNative(JsonElement native) => native.Clone();
        public int GetContextBytes(AgentRequest request) => throw new InvalidOperationException("Reconnect this saved provider/account and restart the companion to resume.");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Saved provider is unavailable.");
        public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken) => throw new InvalidOperationException("Saved provider is unavailable.");
    }
    public sealed record IdArgs(string Id);
    public sealed record TextArgs(string Id, string Text);
    public sealed record DraftArgs(string Id, string Text, long? Revision = null);
    public sealed record ProviderArgs(string Provider, string? AccountId = null);
    public sealed record CreateArgs(string Name, string Provider, string Model, string? AccountId = null);
    public sealed record RunArgs(string Id, string? Message, AgentRunOptions Options, bool Confirmed = false,
        bool FullAccessAcknowledged = false, string? QueuedMessageId = null, long? ExpectedQueueRevision = null, long? PlanRevision = null);
    public sealed record SubmitArgs(string Id, string Text, AgentMessageDelivery Delivery = AgentMessageDelivery.Queue,
        string? ClientMessageId = null, AgentRunOptions? Options = null, bool FullAccessAcknowledged = false, long? DraftRevision = null);
    public sealed record ModeArgs(string Id, AgentCollaborationMode Mode);
    public sealed record GoalArgs(string Id, string? Objective = null, long? TokenBudget = null);
    public sealed record QueueArgs(string Id, long ExpectedRevision);
    public sealed record QueueMessageArgs(string Id, string MessageId, long ExpectedRevision);
    public sealed record QueueEditArgs(string Id, string MessageId, string Text, long ExpectedRevision);
    public sealed record QueueMoveArgs(string Id, string MessageId, int Index, long ExpectedRevision);
    public sealed record ResponseArgs(string Id, JsonElement Value);
    public sealed record ThreadArgs(string Id, long? BeforeSequence = null, long? AfterSequence = null, int MaximumEntries = 80);
    public sealed record ChangesArgs(string Id, bool LatestRun = false, string? ReviewId = null);
    public sealed record FileReviewArgs(string Id, string Path, string ReviewId, bool LatestRun = false, int MaximumLines = 1000, int FirstRow = 0);
    public sealed record BlockReviewArgs(string Id, string Path, string BlockId, string ReviewId, bool LatestRun = false);
    public sealed record BlockRestoreArgs(string Id, string Path, string BlockId, string ReviewId, long ExpectedRevision, bool LatestRun = false);
    public sealed record CompactArgs(string Id, AgentRunOptions Options, bool Confirmed = false);
    public sealed record RestoreArgs(string Id, string[] Paths, long ExpectedRevision, bool LatestRun = false, string? ReviewId = null);
}
