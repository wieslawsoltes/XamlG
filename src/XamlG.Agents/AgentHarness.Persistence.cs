using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

public sealed partial class AgentHarness
{
    /// <summary>The host atomically stores this private snapshot. Awaited before a tool can have side effects.</summary>
    public Func<AgentSessionSnapshot, CancellationToken, Task>? PersistSession { get; set; }
    private readonly SemaphoreSlim _persistenceGate = new(1);
    public async Task SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (PersistSession is not { } save) return;
        await _persistenceGate.WaitAsync(cancellationToken);
        try { await save(CaptureSession(), cancellationToken); }
        finally { _persistenceGate.Release(); }
    }

    public AgentSessionSnapshot CaptureSession() => new(1, Tasks.Select(task =>
    {
        lock (task.Sync)
        {
            var codec = task.Provider as IAgentProviderState;
            JsonElement Native(object native) => codec?.SaveNative(native) ?? throw new InvalidOperationException("This provider does not support private continuation storage.");
            return new AgentTaskSnapshot
            {
                Id = task.Id, Name = task.Name, Provider = task.ProviderId, Account = codec?.AccountIdentity, Model = task.Model,
                Status = task.Status, StatusReason = task.StatusReason, PreviousWorkspace = task.IsPreviousWorkspace, WorkspaceIdentity = task.WorkspaceIdentity,
                ReportedTokens = task.ReportedTokens, EstimatedTokens = task.EstimatedTokens, LastUsage = task.LastUsage,
                RetryAfterUtc = task.RetryAfterUtc, OutputLimitToExceed = task.OutputLimitToExceed, NativeContextBytes = task.NativeContextBytes,
                PlanRevision = task.PlanRevision, CheckpointCount = task.CheckpointCount, Draft = task.Draft, Plan = task.Plan.ToArray(),
                Events = task.PublicEvents.Where(item => item.Kind != "text_delta").ToArray(), Queue = task.Queue,
                Changes = task.Changes, LatestRunChanges = task.LatestRunChanges, BeforeRun = task.BeforeRun, BeforeLatestRun = task.BeforeLatestRun,
                Messages = task.Messages.Select(message => new AgentStoredMessage(message.Kind, message.Text, message.ToolCallId, message.Native == null ? null : Native(message.Native))).ToArray(),
                ActiveRequestIndex = task.ActiveRequest == null ? null : task.Messages.IndexOf(task.ActiveRequest), UserRequests = task.UserRequests.ToArray(),
                PendingReply = task.PendingReply is { } reply ? new(reply.Text, reply.ToolCalls.ToArray(), reply.Usage, Native(reply.Native)) : null,
                NextTool = task.NextTool, ExecutingToolId = task.ExecutingToolId, Goal = task.Goal, LatestRequest = task.LatestRequest, EnabledTools = task.EnabledTools.ToArray()
            };
        }
    }).ToArray());

    /// <summary>Restores into an empty harness, never restores leases or approval grants, and never runs automatically.</summary>
    public void RestoreSession(AgentSessionSnapshot snapshot, Func<string, string?, IAgentProvider> provider, CancellationToken workspaceLifetime = default, string? workspaceIdentity = null)
    {
        lock (_taskGate)
        {
            if (!_tasks.IsEmpty || _activeLease != null) throw new InvalidOperationException("Restore only into an empty agent session.");
            if (snapshot.Version != 1 || snapshot.Tasks.Count > 8 || snapshot.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Tasks.Count)
                throw new ArgumentException("Unsupported or invalid saved agent session.");
            var restored = new List<AgentTask>();
            foreach (var saved in snapshot.Tasks)
            {
                if (string.IsNullOrWhiteSpace(saved.Id) || saved.Id.Length > 200 || saved.Name.Length > 200 || saved.Model.Length > 200 ||
                    saved.Messages.Count > 25000 || saved.Events.Count > 1200 || saved.Draft.Length > 262144 || saved.ReportedTokens < 0 || saved.EstimatedTokens < 0 ||
                    saved.NextTool < 0 || saved.NextTool > (saved.PendingReply?.ToolCalls.Count ?? 0) || saved.ActiveRequestIndex is < 0 || saved.ActiveRequestIndex >= saved.Messages.Count)
                    throw new ArgumentException("Invalid saved task.");
                var selected = provider(saved.Provider, saved.Account);
                var codec = selected as IAgentProviderState;
                var task = new AgentTask(saved.Id, saved.Name, selected, saved.Model,
                    saved.PreviousWorkspace || saved.WorkspaceIdentity != null && saved.WorkspaceIdentity != workspaceIdentity ? new CancellationToken(true) : workspaceLifetime, saved.WorkspaceIdentity)
                {
                    Status = saved.Status, StatusReason = saved.StatusReason, ReportedTokens = saved.ReportedTokens, EstimatedTokens = saved.EstimatedTokens,
                    LastUsage = saved.LastUsage, RetryAfterUtc = saved.RetryAfterUtc, OutputLimitToExceed = saved.OutputLimitToExceed,
                    NativeContextBytes = saved.NativeContextBytes, PlanRevision = saved.PlanRevision, CheckpointCount = saved.CheckpointCount,
                    Draft = saved.Draft, Plan = saved.Plan.ToArray(), QueueRevision = saved.Queue.Revision,
                    Changes = RebaseReview(saved.Changes), LatestRunChanges = RebaseReview(saved.LatestRunChanges),
                    BeforeRun = saved.BeforeRun, BeforeLatestRun = saved.BeforeLatestRun, Goal = saved.Goal, LatestRequest = saved.LatestRequest
                };
                foreach (var message in saved.Messages)
                    task.Messages.Add(new(message.Kind, message.Text, message.ToolCallId, message.Native is { } native
                        ? codec?.RestoreNative(native) ?? throw new ArgumentException("The saved provider continuation cannot be restored.") : null));
                if (saved.ActiveRequestIndex is { } index) task.ActiveRequest = task.Messages[index];
                task.UserRequests.AddRange(saved.UserRequests); task.FollowUps.AddRange(saved.Queue.Messages);
                task.PublicEvents.AddRange(saved.Events); task.EnabledTools.UnionWith(saved.EnabledTools);
                if (saved.PendingReply is { } pending)
                {
                    // Runtime handles, revisions and unconsumed reviews expire across a reload.
                    // Complete the native batch with explicit recovery results; never replay it.
                    foreach (var call in pending.ToolCalls.Skip(saved.NextTool))
                        task.Messages.Add(new(AgentMessageKind.ToolResult, AutomationJson.Element(new { error = call.Id == saved.ExecutingToolId
                            ? "Session interrupted during this operation; effects may have occurred. Inspect current state before proceeding."
                            : "Session restored; this operation was not resumed. Changes may have occurred after the saved checkpoint. Inspect current source and runtime before issuing a fresh call." }).GetRawText(), call.Id));
                }
                if (saved.PendingReply != null || task.Status is AgentTaskStatus.Preparing or AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingAnswer)
                {
                    task.Status = AgentTaskStatus.Paused;
                    task.StatusReason = "Restored after interruption. Review and resume with fresh permissions; inspect current source and preview before edits.";
                }
                restored.Add(task);
            }
            foreach (var task in restored) _tasks.TryAdd(task.Id, task);
            _sequence = Math.Max(_sequence, restored.SelectMany(task => task.Events).Select(item => item.Sequence).DefaultIfEmpty().Max());
        }
    }

    private static AgentChangeReview? RebaseReview(AgentChangeReview? review) => review == null ? null : new(review.Revision, review.Files);

    /// <summary>Rebind only tasks belonging to the same saved project, after the owner reconnects.</summary>
    public bool ReconnectWorkspace(string identity, CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested(); var changed = false;
        foreach (var task in Tasks.Where(task => task.WorkspaceIdentity == identity && task.WorkspaceLifetime != lifetime))
        {
            lock (task.Sync)
            {
                if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.Preparing or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingAnswer) continue;
                if (task.PendingReply is { } reply)
                {
                    foreach (var call in reply.ToolCalls.Skip(task.NextTool)) task.Messages.Add(new(AgentMessageKind.ToolResult,
                        "{\"error\":\"Preview connection changed. This pending operation was not resumed. Inspect current source and runtime before issuing a fresh operation.\"}", call.Id));
                    task.PendingReply = null; task.NextTool = 0; task.ExecutingToolId = null;
                }
                task.WorkspaceLifetime = lifetime; changed = true;
            }
        }
        return changed;
    }
}
