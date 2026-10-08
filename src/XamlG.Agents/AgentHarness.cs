using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

/// <summary>
/// One serialized agent runner per IDE adapter. Provider continuations are committed only
/// after a valid terminal reply; completed tool results are retained before the next request.
/// </summary>
public sealed partial class AgentHarness(IAutomationHost host, IAgentWorkspace? workspace = null, AgentPermissionConstraints? constraints = null) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentTask> _tasks = new(StringComparer.Ordinal);
    private readonly object _taskGate = new();
    private readonly SemaphoreSlim _runGate = new(1);
    private AutomationLease? _activeLease;
    private string? _activeTaskId;
    public AgentPermissionConstraints Constraints { get; } = constraints ?? new();
    public AgentActivePermissions? ActivePermissions => _activeLease is { } lease && lease.IsActive && _activeTaskId is { } id
        ? new(id, lease.Policy, lease.ExpiresAt, lease.GrantedTools) : null;
    public bool RevokeGrant(string taskId, string tool) => _activeTaskId == taskId && _activeLease?.RevokeTool(tool) == true;
    public AgentRunOptions ValidateOptions(AgentRunOptions options, bool requireAcknowledgement = true)
    {
        ArgumentNullException.ThrowIfNull(options); options.Limits.Validate(); options.Compaction.Validate();
        return options with { Policy = Constraints.Apply(options, ToolCatalog, requireAcknowledgement) };
    }
    public IReadOnlyList<AutomationTool> ToolCatalog => host.Tools.Concat(LocalToolDescriptions).ToArray();
    private static readonly IReadOnlyList<AutomationTool> LocalToolDescriptions = DescribeLocalTools();
    private static IReadOnlyList<AutomationTool> DescribeLocalTools()
    {
        var catalog = new AutomationCatalog();
        catalog.Add<PlanArguments, object>("xamlg_agent_plan", "Replace the task's revision-checked plan, with at most 12 steps and one in progress.", AutomationScope.Agent, AutomationEffect.Read, (_, _) => throw new InvalidOperationException());
        catalog.Add<AgentQuestion, object>("xamlg_agent_question", "Ask for task information. Answers never authorize tool operations.", AutomationScope.Agent, AutomationEffect.Read, (_, _) => throw new InvalidOperationException());
        catalog.Add<ToolDiscoveryArguments, object>("xamlg_agent_tools", DiscoveryDescription, AutomationScope.Agent, AutomationEffect.Read, (_, _) => throw new InvalidOperationException());
        return catalog.Tools;
    }
    private long _sequence;
    private bool _disposed;
    public IReadOnlyList<AgentTask> Tasks => _tasks.Values.ToArray();
    public event Action<AgentEvent>? EventPublished;

    public AgentTask CreateTask(string name, IAgentProvider provider, string model, CancellationToken workspaceLifetime = default)
    {
        lock (_taskGate)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tasks.Count >= 8) throw new InvalidOperationException("At most eight tasks can be retained.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || string.IsNullOrWhiteSpace(model) || model.Length > 200)
            throw new ArgumentException("A task name and model ID of at most 200 characters are required.");
        workspaceLifetime.ThrowIfCancellationRequested();
        var task = new AgentTask(Guid.NewGuid().ToString("N"), name, provider, model, workspaceLifetime);
        _tasks.TryAdd(task.Id, task); return task;
        }
    }
    public void RenameTask(string id, string name)
    {
        var task = GetTask(id); EnsureIdle(task);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException("Invalid task name.");
        task.Name = name;
    }
    public void DeleteTask(string id) { EnsureIdle(GetTask(id)); _tasks.TryRemove(id, out _); }
    public AgentTask GetTask(string id) => _tasks.TryGetValue(id, out var task) ? task : throw new KeyNotFoundException("Unknown task.");
    public void Stop() => _activeLease?.Revoke();

    public Task RunAsync(string id, string? message, AgentRunOptions options,
        Func<AutomationReview, CancellationToken, Task<AgentApproval>>? review = null,
        Func<AgentQuestion, CancellationToken, Task<string>>? askUser = null,
        CancellationToken cancellationToken = default) => RunCoreAsync(id, message, null, options, review, askUser, cancellationToken);

    /// <summary>Accepts exactly the reviewed queue revision into a new run. A failed
    /// preflight leaves the queue and native history intact; accepted messages are not replayed.</summary>
    public Task RunQueuedAsync(string id, string messageId, long expectedRevision, AgentRunOptions options,
        Func<AutomationReview, CancellationToken, Task<AgentApproval>>? review = null,
        Func<AgentQuestion, CancellationToken, Task<string>>? askUser = null,
        CancellationToken cancellationToken = default) => RunCoreAsync(id, null, new(messageId, expectedRevision), options, review, askUser, cancellationToken);

    private async Task RunCoreAsync(string id, string? message, QueuedRun? queuedRun, AgentRunOptions options,
        Func<AutomationReview, CancellationToken, Task<AgentApproval>>? review,
        Func<AgentQuestion, CancellationToken, Task<string>>? askUser, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); options = ValidateOptions(options);
        var task = GetTask(id);
        if (!await _runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An agent is already running in this IDE.");
        var started = false;
        var previousStatus = task.Status;
        var preparing = false;
        try
        {
            EnsureIdle(task); EnsureCurrentWorkspace(task);
            if (task.RetryAfterUtc > DateTimeOffset.UtcNow) throw new InvalidOperationException($"The provider cooldown lasts until {task.RetryAfterUtc:O}. Resume after that deadline.");
            if (task.OutputLimitToExceed is { } output && options.Limits.OutputTokensPerRequest <= output)
                throw new InvalidOperationException($"Raise the output allowance above {output:N0} tokens before resuming.");
            if (task.Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled) throw new InvalidOperationException("Start a new task after denial, cancellation or an invalid response.");
            using var workspaceRun = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.WorkspaceLifetime,
                task.Provider is IAgentProviderSession session ? session.GetSessionLifetime() : default);
            using var lease = new AutomationLease(options.Policy, options.LeaseDuration, workspaceRun.Token);
            lease.Token.ThrowIfCancellationRequested();
            lock (task.Sync)
            {
                if (queuedRun != null)
                    message = task.FollowUps[QueuedIndex(task, queuedRun.Id, queuedRun.Revision)].Text;
                if (message != null)
                {
                    if (task.PendingReply != null || task.Status == AgentTaskStatus.Paused) throw new InvalidOperationException("Resume the pending turn before adding a follow-up.");
                    if (string.IsNullOrWhiteSpace(message) || message.Length > 262144) throw new ArgumentException("A bounded, nonempty task message is required.");
                    if (task.UserRequests.Sum(request => (long)request.Length) + message.Length > 2_000_000)
                        throw new InvalidOperationException("Retained user requests reached the task limit. Create a new task with a reviewed context handoff.");
                }
                else if (task.Status != AgentTaskStatus.Paused) throw new InvalidOperationException("Only a paused task can be resumed without a new message.");
                task.Status = AgentTaskStatus.Preparing; preparing = true;
            }
            _activeLease = lease; _activeTaskId = id;
            // Checkpoint and catalog failures must not consume the reviewed message.
            var checkpoint = workspace != null && (message != null || task.BeforeRun == null)
                ? await CaptureWorkspaceAsync(lease.Token) : null;
            var local = LocalTools(task, askUser, lease.Token);
            var allTools = host.Tools.Concat(local.Tools).ToArray();
            var tools = SelectTools(task, allTools, options.FullToolCatalog);
            var catalog = allTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
            lease.Token.ThrowIfCancellationRequested(); EnsureCurrentWorkspace(task);
            lock (task.Sync)
            {
                var queuedIndex = queuedRun == null ? -1 : QueuedIndex(task, queuedRun.Id, queuedRun.Revision);
                if (message != null)
                {
                    task.Goal ??= message; task.LatestRequest = message;
                    task.UserRequests.Add(message); task.ActiveRequest = new(AgentMessageKind.User, message); task.Messages.Add(task.ActiveRequest);
                    if (queuedIndex >= 0) { task.FollowUps.RemoveAt(queuedIndex); task.QueueRevision++; }
                }
                task.BeforeRun ??= checkpoint;
                if (checkpoint != null) task.BeforeLatestRun = checkpoint;
                started = true; task.Status = AgentTaskStatus.Running; task.StatusReason = null;
            }
            if (message != null) Publish(task, "user", message);
            await SaveSessionAsync(lease.Token);
            var budget = new RunBudget(); var calls = 0;
            while (true)
            {
                lease.Token.ThrowIfCancellationRequested();
                if (task.TotalTokens >= options.Limits.TotalTaskTokens) { Pause(task, "Task token budget reached."); return; }
                if (task.PendingReply == null)
                {
                    tools = SelectTools(task, allTools, options.FullToolCatalog);
                    var request = new AgentRequest(task.Model, RunInstructions(options), task.Messages.ToArray(), tools, options.Limits.OutputTokensPerRequest);
                    task.NativeContextBytes = task.Provider.GetContextBytes(request);
                    if (NeedsCompaction(task, options) && options.AutomaticCompaction && task.Messages.Count > 1)
                    {
                        if (!await CompactContextAsync(task, options, tools, lease, budget)) return;
                        request = request with { Messages = task.Messages.ToArray() };
                        task.NativeContextBytes = task.Provider.GetContextBytes(request);
                    }
                    if (task.NativeContextBytes > options.Limits.ContextBytes || _tasks.Values.Sum(value => (long)value.NativeContextBytes) > 64_000_000)
                    { Pause(task, "Request or retained task context limit reached. Compact context, delete unused tasks or raise the request limit."); return; }
                    if (options.Compaction.ModelContextWindowTokens > 0 && (task.NativeContextBytes + 3L) / 4 + options.Limits.OutputTokensPerRequest > options.Compaction.ModelContextWindowTokens)
                    { Pause(task, "Estimated input plus output reserve exceeds the configured model context window."); return; }
                    var reply = await RequestProviderAsync(task, request, options, lease, budget, contextBytes: task.NativeContextBytes);
                    if (reply == null) return;
                    lease.Token.ThrowIfCancellationRequested();
                    // Validate the entire batch before committing it or executing a single operation.
                    if (reply.ToolCalls.Count > 1024 || reply.ToolCalls.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != reply.ToolCalls.Count)
                        throw new AutomationException("invalid_response", "Invalid tool batch.");
                    foreach (var call in reply.ToolCalls)
                    {
                        if (string.IsNullOrWhiteSpace(call.Id) || !catalog.TryGetValue(call.Name, out var tool))
                            throw new AutomationException("invalid_response", "Unknown tool or missing call identity.");
                        AutomationSchema.Validate(tool.InputSchema, call.Arguments);
                    }
                    lock (task.Sync) task.Messages.Add(new(AgentMessageKind.Assistant, reply.Text, Native: reply.Native));
                    task.NativeContextBytes = task.Provider.GetContextBytes(request with { Messages = task.Messages.ToArray() });
                    Publish(task, "assistant", reply.Text);
                    if (reply.ToolCalls.Count == 0)
                    {
                        task.ActiveRequest = null;
                        task.Status = AgentTaskStatus.Completed; Publish(task, "completed", "Task response completed."); return;
                    }
                    task.PendingReply = reply; task.NextTool = 0;
                    await SaveSessionAsync(lease.Token);
                }

                var pending = task.PendingReply;
                if (pending.ToolCalls.Count - task.NextTool > options.Limits.ToolsPerRun - calls)
                { Pause(task, "Tool-call budget cannot fit the pending batch. Review the limit and resume."); return; }
                if (!ReserveToolResults(task, options, tools)) return;
                while (task.NextTool < pending.ToolCalls.Count)
                {
                    lease.Token.ThrowIfCancellationRequested();
                    var call = pending.ToolCalls[task.NextTool]; var tool = catalog[call.Name];
                    var decision = lease.Decide(tool);
                    if (decision == PermissionDecision.Deny) throw new AutomationException("permission_denied", "The run policy denies " + tool.Name);
                    if (decision == PermissionDecision.Ask)
                    {
                        if (review == null) throw new AutomationException("permission_denied", "This operation requires a host review callback.");
                        task.Status = AgentTaskStatus.AwaitingApproval; Publish(task, "approval", tool.Name, call.Id);
                        JsonElement? preview = null;
                        if (workspace is IAgentOperationPreview planner)
                        {
                            try { preview = await planner.PreviewOperationAsync(tool.Name, call.Arguments, lease.Token); }
                            catch (Exception error) when (error is AutomationException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                            { preview = AutomationJson.Element(new { sourcePreview = false, note = "The operation preview could not be produced. Re-read current source before approving.", files = Array.Empty<object>() }); }
                        }
                        var answer = await review(new(tool, call.Arguments, "AI Agent", preview), lease.Token);
                        lease.Token.ThrowIfCancellationRequested(); task.Status = AgentTaskStatus.Running;
                        if (!Enum.IsDefined(answer) || answer == AgentApproval.AllowToolForRun && !Constraints.AllowRunApprovals)
                            throw new AutomationException("permission_denied", "The host does not allow this approval grant.");
                        if (answer == AgentApproval.Deny) throw new AutomationException("permission_denied", "Operation denied by the user.");
                        if (answer == AgentApproval.AllowToolForRun) lease.GrantTool(tool.Name);
                    }
                    calls++; task.ExecutingToolId = call.Id;
                    if (tool.Effect != AutomationEffect.Read) await SaveSessionAsync(lease.Token);
                    Publish(task, "tool_started", tool.Name, call.Id, tool.Name);
                    JsonElement result;
                    try
                    {
                        var target = local.Tools.Any(t => t.Name == tool.Name) ? (IAutomationHost)local : host;
                        result = await target.CallAsync(tool.Name, call.Arguments, new("AI Agent", lease.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        lock (task.Sync) { task.Messages.Add(new(AgentMessageKind.ToolResult, "{\"error\":\"Operation cancelled; effects may have occurred. Inspect current state before another operation.\"}", call.Id)); task.NextTool++; task.ExecutingToolId = null; }
                        throw;
                    }
                    catch (Exception error) when (error is AutomationException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                    { result = AutomationJson.Element(new { error = new { code = (error as AutomationException)?.Code ?? "operation_failed", message = error.Message } }); }
                    var text = result.GetRawText();
                    if (Encoding.UTF8.GetByteCount(text) > task.PendingResultBytes)
                        text = "{\"error\":\"Tool result exceeds the configured byte limit. Request smaller ranges. The operation ran and will not be replayed.\",\"omitted\":true}";
                    // Commit the result and cursor before observers or another provider request.
                    lock (task.Sync) { task.Messages.Add(new(AgentMessageKind.ToolResult, text, call.Id)); task.NextTool++; task.ExecutingToolId = null; }
                    if (AutomationMedia.TryRead(text, out var media)) Publish(task, "tool_completed", media.Metadata.GetRawText(), call.Id, tool.Name, media.Images);
                    else Publish(task, "tool_completed", text, call.Id, tool.Name);
                    if (tool.Effect != AutomationEffect.Read) await SaveSessionAsync(lease.Token);
                }
                task.PendingReply = null; task.NextTool = 0;
                await SaveSessionAsync(lease.Token);
            }
        }
        catch (OperationCanceledException) when (started) { task.Status = AgentTaskStatus.Cancelled; task.StatusReason = "Stopped, revoked or lease expired."; Publish(task, "cancelled", task.StatusReason); }
        catch (Exception error) when (started)
        { task.Status = AgentTaskStatus.Failed; task.StatusReason = error.Message; Publish(task, "failed", error.Message); throw; }
        catch (Exception error)
        { task.StatusReason = error.Message; Publish(task, "run_rejected", error.Message); throw; }
        finally
        {
            if (!started && preparing) task.Status = previousStatus;
            if (started && workspace != null && task.BeforeRun != null && !task.IsPreviousWorkspace)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(task.WorkspaceLifetime);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    var after = await CaptureWorkspaceAsync(deadline.Token);
                    task.Changes = new(after.Revision, Diff(task.BeforeRun.Documents, after.Documents));
                    if (task.BeforeLatestRun != null) task.LatestRunChanges = new(after.Revision, Diff(task.BeforeLatestRun.Documents, after.Documents));
                    Publish(task, "changes", $"{task.Changes.Files.Count} source files changed.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Publish(task, "changes_unavailable", "The source checkpoint could not be refreshed. Inspect the current project before restoring."); }
            }
            try { if (started) await SaveSessionAsync(); }
            finally { _activeLease = null; _activeTaskId = null; _runGate.Release(); }
        }
    }

    private string RunInstructions(AgentRunOptions options) => options.Instructions +
        (options.FullToolCatalog ? "" : "\nUse xamlg_agent_tools to search and enable additional IDE tools when needed. Enabled schemas arrive on the next request; discover tools before guessing names or arguments.") +
        "\nThe embedding host enforces this run's permission policy: " + JsonSerializer.Serialize(new { options.Policy, constraints = Constraints }, AutomationJson.Options) +
        "\nA denied operation ends the batch. Do not try a different operation to bypass a denied effect." +
        (options.Policy.Profile == PermissionProfile.Plan ? "\nThis is a planning run. Inspect and propose a plan; implementation requires a separately confirmed editing run." : "");

    private AutomationCatalog LocalTools(AgentTask task, Func<AgentQuestion, CancellationToken, Task<string>>? askUser, CancellationToken token)
    {
        var catalog = new AutomationCatalog();
        AddDiscoveryTool(catalog, task);
        catalog.Add<PlanArguments, object>("xamlg_agent_plan", "Replace this task's revision-checked plan. At most 12 steps and one in progress.", AutomationScope.Agent, AutomationEffect.Read,
            (args, _) =>
            {
                if (args.ExpectedRevision != task.PlanRevision) throw new AutomationException("revision_conflict", "Plan revision changed.");
                if (args.Steps.Length is < 1 or > 12 || args.Steps.Count(s => s.Status == AgentStepStatus.InProgress) > 1 ||
                    args.Steps.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Text) || s.Text.Length > 2000) ||
                    args.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != args.Steps.Length)
                    throw new ArgumentException("Invalid plan steps.");
                task.Plan = args.Steps.ToArray(); task.PlanRevision++;
                Publish(task, "plan", JsonSerializer.Serialize(task.Plan, AutomationJson.Options));
                return ValueTask.FromResult<object>(new { revision = task.PlanRevision, steps = task.Plan });
            });
        if (askUser != null)
            catalog.Add<AgentQuestion, object>("xamlg_agent_question", "Ask for task information. Answers never authorize tool operations.", AutomationScope.Agent, AutomationEffect.Read,
                async (question, _) =>
                {
                    if (string.IsNullOrWhiteSpace(question.Question) || question.Question.Length > 4000 || question.Options?.Count > 8)
                        throw new ArgumentException("Invalid question.");
                    task.Status = AgentTaskStatus.AwaitingAnswer; Publish(task, "question", question.Question);
                    var answer = await askUser(question, token); token.ThrowIfCancellationRequested();
                    if (answer.Length > 16384) throw new ArgumentException("Answer exceeds the limit.");
                    task.Status = AgentTaskStatus.Running; Publish(task, "answer", answer);
                    return new { answer };
                });
        return catalog;
    }

    public string ExportTranscript(string id) => JsonSerializer.Serialize(new
    {
        task = GetTask(id).Name, provider = GetTask(id).ProviderId, model = GetTask(id).Model,
        reportedTokens = GetTask(id).ReportedTokens, estimatedTokens = GetTask(id).EstimatedTokens, events = GetTask(id).Events
    }, AutomationJson.Options);

    private static void EnsureIdle(AgentTask task)
    { if (task.Status is AgentTaskStatus.Preparing or AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingAnswer) throw new InvalidOperationException("This task is running."); }
    private static void EnsureCurrentWorkspace(AgentTask task)
    { if (task.IsPreviousWorkspace) throw new InvalidOperationException("This task belongs to a previous workspace. Create a new task for the current project."); }
    private void Pause(AgentTask task, string reason) { task.Status = AgentTaskStatus.Paused; task.StatusReason = reason; Publish(task, "paused", reason); }
    private void Publish(AgentTask task, string kind, string text, string? callId = null, string? toolName = null, IReadOnlyList<AutomationImage>? images = null)
    {
        if (text.Length > 262144) text = text[..262144] + "\n[public text truncated]";
        var item = new AgentEvent(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, task.Id, kind, text, callId) { ToolName = toolName, Images = images };
        if (kind != "text_delta") lock (task.Sync)
        {
        task.PublicEvents.Add(item);
        while (task.PublicEvents.Count > 1200 || task.PublicEvents.Sum(e => (long)e.Text.Length + (e.Images?.Sum(image => (long)image.Data.Length) ?? 0)) > 4_000_000)
            task.PublicEvents.RemoveAt(0);
        }
        // UI/transport observers cannot interrupt a committed tool operation or strand a run.
        if (EventPublished is { } published)
            foreach (Action<AgentEvent> observer in published.GetInvocationList())
                try { observer(item); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    public sealed record PlanArguments(long ExpectedRevision, AgentPlanStep[] Steps);
    private sealed record QueuedRun(string Id, long Revision);
}

public sealed record AgentActivePermissions(string TaskId, AutomationPolicy Policy, DateTimeOffset ExpiresAt, IReadOnlyList<string> GrantedTools);
