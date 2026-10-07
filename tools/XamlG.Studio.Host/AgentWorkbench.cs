using System.Collections.Concurrent;
using System.Text.Json;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using XamlG.Automation;
using XamlG.Mcp;

namespace XamlG.Studio.Host;

/// <summary>Host-owned task and approval state. Cloud credentials never enter browser messages.</summary>
public sealed class AgentWorkbench : IDisposable
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;
    private readonly AgentHarness _harness;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _run;
    private CancellationTokenSource? _runCancellation;
    private string? _runningId;
    private readonly AutomationMcpTaskStore? _mcpTasks;
    private readonly ChatGptAccountManager? _chatGpt;
    private readonly string? _chatGptError;
    public AgentWorkbench(IAutomationHost host, IEnumerable<IAgentProvider> providers, IAgentWorkspace? workspace = null, AutomationMcpTaskStore? mcpTasks = null,
        ChatGptAccountManager? chatGpt = null, string? chatGptError = null)
    { _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal); _harness = new(host, workspace); _mcpTasks = mcpTasks; _chatGpt = chatGpt; _chatGptError = chatGptError; }
    public AgentHarness Harness => _harness;

    public async Task<JsonElement> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken, CancellationToken ownerSession = default)
    {
        switch (action)
        {
            case "state":
                return AutomationJson.Element(new
                {
                    providers = _providers.Keys.Concat(_chatGpt == null ? [] : new[] { ChatGptAccountAgentProvider.ProviderId }).Order(StringComparer.Ordinal),
                    chatGpt = _chatGpt?.State, chatGptError = _chatGptError, tasks = _harness.Tasks.Select(task => new
                    {
                        task.Id, task.Name, task.ProviderId, task.Model, task.Status, task.StatusReason, task.Draft, task.IsPreviousWorkspace,
                        account = task.Provider is ChatGptAccountAgentProvider account ? new { id = account.AccountId, label = account.AccountLabel + " · " + account.AccountId[..8] } : null,
                        task.TotalTokens, task.ReportedTokens, task.EstimatedTokens, task.CheckpointCount, task.Plan, task.PlanRevision, task.Queue,
                        task.LastUsage, task.NativeContextBytes, task.RetryAfterUtc, task.OutputLimitToExceed,
                        changes = task.Changes == null ? null : new { task.Changes.Revision, files = task.Changes.Files.Select(file => new { file.Path, beforeLength = file.Before?.Length, afterLength = file.After?.Length }) },
                        latestRunChanges = task.LatestRunChanges == null ? null : new { task.LatestRunChanges.Revision, files = task.LatestRunChanges.Files.Select(file => new { file.Path, beforeLength = file.Before?.Length, afterLength = file.After?.Length }) },
                        events = task.Events.TakeLast(80).Select(item => item with { Text = item.Text.Length > 8192 ? item.Text[..8192] + "\n[see transcript export]" : item.Text })
                    }),
                    pending = _pending.Values.Select(p => new { p.Id, p.TaskId, p.Kind, p.Content }),
                    operations = _mcpTasks?.LocalInventory.Select(task => new { task.TaskId, status = task.Status.ToString(), task.CreatedAt, task.LastUpdatedAt })
                });
            case "models":
                var modelRequest = Read<ProviderArgs>(arguments);
                return AutomationJson.Element(await Provider(modelRequest.Provider, modelRequest.AccountId).ListModelsAsync(cancellationToken));
            case "model_choices":
                var choices = Read<ProviderArgs>(arguments); var provider = Provider(choices.Provider, choices.AccountId);
                return AutomationJson.Element(provider is ChatGptAccountAgentProvider accountProvider
                    ? await accountProvider.ListModelChoicesAsync(cancellationToken)
                    : (await provider.ListModelsAsync(cancellationToken)).Select(model => new ChatGptModel(model, model)).ToArray());
            case "chatgpt_sign_in":
                var signIn = Read<AccountSignInArgs>(arguments);
                return AutomationJson.Element(await Accounts().BeginSignInAsync(signIn.AccountId, signIn.Label, signIn.Remember, signIn.RetrySignInId, signIn.RequestPlanConsent, ownerSession, cancellationToken));
            case "chatgpt_cancel_sign_in": await Accounts().CancelSignInAsync(Read<IdArgs>(arguments).Id, cancellationToken); break;
            case "chatgpt_select": await Accounts().SelectAsync(Read<IdArgs>(arguments).Id, cancellationToken); break;
            case "chatgpt_configure":
                var configure = Read<AccountConfigureArgs>(arguments);
                await Accounts().ConfigureAsync(configure.Id, configure.Label, configure.Remember, cancellationToken); break;
            case "chatgpt_sign_out": return AutomationJson.Element(await Accounts().SignOutAsync(Read<IdArgs>(arguments).Id, cancellationToken));
            case "operation_cancel": _mcpTasks?.CancelLocal(Read<IdArgs>(arguments).Id); break;
            case "operations_clear": _mcpTasks?.ClearFinishedLocal(); break;
            case "create":
                var create = Read<CreateArgs>(arguments);
                return AutomationJson.Element(_harness.CreateTask(create.Name, Provider(create.Provider, create.AccountId), create.Model, ownerSession));
            case "rename":
                var rename = Read<TextArgs>(arguments); _harness.RenameTask(rename.Id, rename.Text); break;
            case "draft":
                var draft = Read<TextArgs>(arguments);
                if (draft.Text.Length > 262144) throw new ArgumentException("Draft is too large.");
                _harness.GetTask(draft.Id).Draft = draft.Text; break;
            case "delete": _harness.DeleteTask(Read<IdArgs>(arguments).Id); break;
            case "queue":
                var queue = Read<TextArgs>(arguments); _harness.QueueMessage(queue.Id, queue.Text);
                var queuedTask = _harness.GetTask(queue.Id); if (queuedTask.Draft == queue.Text) queuedTask.Draft = "";
                return AutomationJson.Element(queuedTask.Queue);
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
                    _harness.GetTask(compact.Id).Provider is ChatGptAccountAgentProvider compactAccount ? compactAccount.GetSessionLifetime() : default))
                    return AutomationJson.Element(new { compacted = await _harness.CompactAsync(compact.Id, compact.Options, compactionLife.Token) });
            case "stop": Stop(); break;
            case "run":
                var run = Read<RunArgs>(arguments);
                run.Options.Limits.Validate(); var task = _harness.GetTask(run.Id);
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
                else if ((run.Message == null) != (task.Status == AgentTaskStatus.Paused)) throw new InvalidOperationException("Resume a paused task without adding a new message.");
                if (run.Message != null && (string.IsNullOrWhiteSpace(run.Message) || run.Message.Length > 262144)) throw new ArgumentException("A bounded message is required.");
                lock (_gate)
                {
                    if (_run is { IsCompleted: false }) throw new InvalidOperationException("An agent is already running.");
                    ownerSession.ThrowIfCancellationRequested();
                    var accountLife = task.Provider is ChatGptAccountAgentProvider runAccount ? runAccount.GetSessionLifetime() : default;
                    _runCancellation?.Dispose(); _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ownerSession, accountLife);
                    _runningId = run.Id;
                    _run = RunAsync(run, _runCancellation.Token);
                }
                break;
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
            case "diff":
                var diff = Read<ChangesArgs>(arguments); var diffTask = _harness.GetTask(diff.Id);
                return AutomationJson.Element((diff.LatestRun ? diffTask.LatestRunChanges : diffTask.Changes)?.Files.Select(file => AgentSourceReview.Diff(file)).ToArray());
            case "patch":
                var patch = Read<ChangesArgs>(arguments); var patchTask = _harness.GetTask(patch.Id);
                return AutomationJson.Element(AgentSourceReview.Patch((patch.LatestRun ? patchTask.LatestRunChanges : patchTask.Changes) ?? throw new InvalidOperationException("No source review is available.")));
            case "changes":
                var changes = Read<ChangesArgs>(arguments); var changedTask = _harness.GetTask(changes.Id);
                return AutomationJson.Element(changes.LatestRun ? changedTask.LatestRunChanges : changedTask.Changes);
            case "restore":
                var restore = Read<RestoreArgs>(arguments);
                return AutomationJson.Element(await _harness.RestoreChangesAsync(restore.Id, restore.Paths, restore.ExpectedRevision, cancellationToken, restore.LatestRun));
            default: throw new ArgumentException("Unknown agent action.");
        }
        return AutomationJson.Element(new { accepted = true });
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
            if (args.QueuedMessageId != null)
                await _harness.RunQueuedAsync(args.Id, args.QueuedMessageId, args.ExpectedQueueRevision!.Value, args.Options, Review, Question, cancellationToken);
            else await _harness.RunAsync(args.Id, args.Message, args.Options, Review, Question, cancellationToken);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { /* The harness retains the failure in task status and its public thread. */ }
        finally { lock (_gate) _runningId = null; }
    }

    private async Task<JsonElement> AskAsync(string kind, JsonElement content, CancellationToken token)
    {
        var item = new Pending(Guid.NewGuid().ToString("N"), _runningId!, kind, content);
        _pending.TryAdd(item.Id, item);
        try { return await item.Completion.Task.WaitAsync(token); }
        finally { _pending.TryRemove(item.Id, out _); }
    }

    private ChatGptAccountManager Accounts() => _chatGpt ?? throw new InvalidOperationException(_chatGptError ?? "ChatGPT account mode is disabled in the companion.");
    private IAgentProvider Provider(string id, string? accountId = null) => id == ChatGptAccountAgentProvider.ProviderId ?
        Accounts().CreateProvider(accountId ?? throw new ArgumentException("Select an explicit ChatGPT account for this request.")) :
        _providers.TryGetValue(id, out var provider) ? provider : throw new ArgumentException("Provider is not configured in the companion.");
    private static T Read<T>(JsonElement arguments) => arguments.Deserialize<T>(AutomationJson.Options) ?? throw new ArgumentException("Arguments are required.");
    public void Stop() { lock (_gate) _runCancellation?.Cancel(); _harness.Stop(); }
    public void Dispose() { _lifetime.Cancel(); _harness.Dispose(); _runCancellation?.Dispose(); _lifetime.Dispose(); }
    private sealed record Pending(string Id, string TaskId, string Kind, JsonElement Content)
    { public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public sealed record IdArgs(string Id);
    public sealed record TextArgs(string Id, string Text);
    public sealed record ProviderArgs(string Provider, string? AccountId = null);
    public sealed record AccountSignInArgs(string? AccountId = null, string? Label = null, bool Remember = false, string? RetrySignInId = null, bool RequestPlanConsent = false);
    public sealed record AccountConfigureArgs(string Id, string Label, bool Remember);
    public sealed record CreateArgs(string Name, string Provider, string Model, string? AccountId = null);
    public sealed record RunArgs(string Id, string? Message, AgentRunOptions Options, bool Confirmed = false,
        bool FullAccessAcknowledged = false, string? QueuedMessageId = null, long? ExpectedQueueRevision = null);
    public sealed record QueueArgs(string Id, long ExpectedRevision);
    public sealed record QueueMessageArgs(string Id, string MessageId, long ExpectedRevision);
    public sealed record QueueEditArgs(string Id, string MessageId, string Text, long ExpectedRevision);
    public sealed record QueueMoveArgs(string Id, string MessageId, int Index, long ExpectedRevision);
    public sealed record ResponseArgs(string Id, JsonElement Value);
    public sealed record ChangesArgs(string Id, bool LatestRun = false);
    public sealed record CompactArgs(string Id, AgentRunOptions Options, bool Confirmed = false);
    public sealed record RestoreArgs(string Id, string[] Paths, long ExpectedRevision, bool LatestRun = false);
}

public sealed class BrowserAgentWorkspace(IAutomationHost host) : IAgentWorkspace
{
    public async Task<AgentWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var value = await host.CallAsync("xamlg_project_export", AutomationJson.Element(new { }), new("Agent checkpoint", cancellationToken));
        return new(value.GetProperty("revision").GetInt64(), value.GetProperty("documents").Deserialize<Dictionary<string, string>>(AutomationJson.Options)!);
    }
    public async Task<AgentWorkspaceSnapshot> RestoreAsync(long expectedRevision, IReadOnlyList<AgentFileChange> files, CancellationToken cancellationToken)
    {
        var value = await host.CallAsync("xamlg_project_restore", AutomationJson.Element(new { expectedRevision, files }), new("Agent change review", cancellationToken));
        return new(value.GetProperty("revision").GetInt64(), value.GetProperty("documents").Deserialize<Dictionary<string, string>>(AutomationJson.Options)!);
    }
}
