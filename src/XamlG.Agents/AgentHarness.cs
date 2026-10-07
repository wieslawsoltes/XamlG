using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

/// <summary>
/// One serialized agent runner per IDE adapter. Provider continuations are committed only
/// after a valid terminal reply; completed tool results are retained before the next request.
/// </summary>
public sealed partial class AgentHarness(IAutomationHost host, IAgentWorkspace? workspace = null) : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentTask> _tasks = new(StringComparer.Ordinal);
    private readonly object _taskGate = new();
    private readonly SemaphoreSlim _runGate = new(1);
    private AutomationLease? _activeLease;
    private long _sequence;
    private bool _disposed;
    public IReadOnlyList<AgentTask> Tasks => _tasks.Values.ToArray();
    public event Action<AgentEvent>? EventPublished;

    public AgentTask CreateTask(string name, IAgentProvider provider, string model)
    {
        lock (_taskGate)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tasks.Count >= 8) throw new InvalidOperationException("At most eight tasks can be retained.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || string.IsNullOrWhiteSpace(model) || model.Length > 200)
            throw new ArgumentException("A task name and model ID of at most 200 characters are required.");
        var task = new AgentTask(Guid.NewGuid().ToString("N"), name, provider, model);
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

    public async Task RunAsync(string id, string? message, AgentRunOptions options,
        Func<AutomationReview, CancellationToken, Task<AgentApproval>>? review = null,
        Func<AgentQuestion, CancellationToken, Task<string>>? askUser = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); options.Limits.Validate();
        var task = GetTask(id);
        if (!await _runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An agent is already running in this IDE.");
        try
        {
            EnsureIdle(task);
            if (task.Status is AgentTaskStatus.Failed or AgentTaskStatus.Cancelled) throw new InvalidOperationException("Start a new task after denial, cancellation or an invalid response.");
            if (message != null)
            {
                if (task.PendingReply != null || task.Status == AgentTaskStatus.Paused) throw new InvalidOperationException("Resume the pending turn before adding a follow-up.");
                if (string.IsNullOrWhiteSpace(message) || message.Length > 262144) throw new ArgumentException("A bounded, nonempty task message is required.");
                task.Goal ??= message; task.LatestRequest = message;
                task.UserRequests.Add(message);
                task.Messages.Add(new(AgentMessageKind.User, message)); Publish(task, "user", message);
            }
            else if (task.Status != AgentTaskStatus.Paused) throw new InvalidOperationException("Only a paused task can be resumed without a new message.");
            using var lease = new AutomationLease(options.Policy, options.LeaseDuration, cancellationToken);
            _activeLease = lease; task.Status = AgentTaskStatus.Running; task.StatusReason = null;
            if (workspace != null && task.BeforeRun == null)
                task.BeforeRun = await CaptureWorkspaceAsync(lease.Token);
            var local = LocalTools(task, askUser, lease.Token);
            var tools = host.Tools.Concat(local.Tools).ToArray();
            var catalog = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
            var requests = 0; var calls = 0;
            while (true)
            {
                lease.Token.ThrowIfCancellationRequested();
                if (task.TotalTokens >= options.Limits.TotalTaskTokens) { Pause(task, "Task token budget reached."); return; }
                if (task.PendingReply == null)
                {
                    var request = new AgentRequest(task.Model, options.Instructions, task.Messages.ToArray(), tools, options.Limits.OutputTokensPerRequest);
                    task.ContextBytes = task.Provider.GetContextBytes(request);
                    if (task.ContextBytes > options.Limits.ContextBytes && options.AutomaticCompaction && task.Messages.Count > 1)
                    {
                        try { CompactContext(task, Math.Min(1_000_000, options.Limits.ContextBytes / 2)); }
                        catch (InvalidOperationException error) { Pause(task, error.Message); return; }
                        request = request with { Messages = task.Messages.ToArray() };
                        task.ContextBytes = task.Provider.GetContextBytes(request);
                    }
                    if (task.ContextBytes > options.Limits.ContextBytes) { Pause(task, "Request context limit reached. Compact context or raise the limit."); return; }
                    AgentReply reply;
                    var retry = 0;
                    while (true)
                    {
                        if (requests >= options.Limits.RequestsPerRun) { Pause(task, "Request limit reached."); return; }
                        requests++;
                        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
                        requestTimeout.CancelAfter(options.Limits.RequestTimeout);
                        try
                        {
                            Publish(task, "request", $"Request {requests}");
                            reply = await task.Provider.GenerateAsync(request, text =>
                            { Publish(task, "text_delta", text); return ValueTask.CompletedTask; }, requestTimeout.Token);
                            break;
                        }
                        catch (AgentProviderException error)
                        {
                            if (!error.Retryable && (error.Code.StartsWith("invalid_", StringComparison.Ordinal) || error.Code is "duplicate_terminal_event" or "response_too_large")) throw;
                            if (!error.Retryable || retry >= options.Limits.AutomaticRetries || error.RetryAfter > TimeSpan.FromSeconds(30))
                            { Pause(task, error.Message); return; }
                            var delay = error.RetryAfter ?? TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, retry)) + Random.Shared.Next(250));
                            retry++; Publish(task, "retry", $"Retry {retry}: {error.Code}");
                            await Task.Delay(delay, lease.Token);
                        }
                        catch (OperationCanceledException) when (!lease.Token.IsCancellationRequested)
                        { Pause(task, "Provider request timed out. Resume preserves completed operations."); return; }
                    }
                    if (reply.Usage.InputTokens < 0 || reply.Usage.OutputTokens < 0) throw new AutomationException("invalid_response", "Invalid provider usage.");
                    if (reply.Usage.Estimated) task.EstimatedTokens = checked(task.EstimatedTokens + reply.Usage.Total);
                    else task.ReportedTokens = checked(task.ReportedTokens + reply.Usage.Total);
                    if (reply.OutputLimitReached)
                    { Pause(task, "Provider output limit reached. Raise the output limit and resume; the incomplete reply was not committed."); return; }
                    // Validate the entire batch before committing it or executing a single operation.
                    if (reply.ToolCalls.Count > 1024 || reply.ToolCalls.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != reply.ToolCalls.Count)
                        throw new AutomationException("invalid_response", "Invalid tool batch.");
                    foreach (var call in reply.ToolCalls)
                    {
                        if (string.IsNullOrWhiteSpace(call.Id) || !catalog.TryGetValue(call.Name, out var tool))
                            throw new AutomationException("invalid_response", "Unknown tool or missing call identity.");
                        AutomationSchema.Validate(tool.InputSchema, call.Arguments);
                    }
                    task.Messages.Add(new(AgentMessageKind.Assistant, reply.Text, Native: reply.Native));
                    Publish(task, "assistant", reply.Text);
                    if (reply.ToolCalls.Count == 0)
                    {
                        string? followUp;
                        lock (task.Sync) followUp = task.FollowUps.TryDequeue(out var queued) ? queued : null;
                        if (followUp != null)
                        {
                            task.LatestRequest = followUp; task.Messages.Add(new(AgentMessageKind.User, followUp));
                            task.UserRequests.Add(followUp);
                            Publish(task, "user", followUp); continue;
                        }
                        task.Status = AgentTaskStatus.Completed; Publish(task, "completed", "Task response completed."); return;
                    }
                    task.PendingReply = reply; task.NextTool = 0;
                }

                var pending = task.PendingReply;
                if (pending.ToolCalls.Count - task.NextTool > options.Limits.ToolsPerRun - calls)
                { Pause(task, "Tool-call budget cannot fit the pending batch. Review the limit and resume."); return; }
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
                        var answer = await review(new(tool, call.Arguments, "AI Agent"), lease.Token);
                        lease.Token.ThrowIfCancellationRequested(); task.Status = AgentTaskStatus.Running;
                        if (answer == AgentApproval.Deny) throw new AutomationException("permission_denied", "Operation denied by the user.");
                        if (answer == AgentApproval.AllowToolForRun) lease.GrantTool(tool.Name);
                    }
                    calls++; Publish(task, "tool_started", tool.Name, call.Id);
                    JsonElement result;
                    try
                    {
                        var target = local.Tools.Any(t => t.Name == tool.Name) ? (IAutomationHost)local : host;
                        result = await target.CallAsync(tool.Name, call.Arguments, new("AI Agent", lease.Token));
                    }
                    catch (OperationCanceledException)
                    {
                        task.Messages.Add(new(AgentMessageKind.ToolResult, "{\"error\":\"Operation cancelled; effects may have occurred. Inspect current state before another operation.\"}", call.Id));
                        task.NextTool++; throw;
                    }
                    catch (Exception error) when (error is AutomationException or ArgumentException or InvalidOperationException or KeyNotFoundException)
                    { result = AutomationJson.Element(new { error = new { code = (error as AutomationException)?.Code ?? "operation_failed", message = error.Message } }); }
                    var text = result.GetRawText();
                    if (Encoding.UTF8.GetByteCount(text) > options.Limits.ToolResultBytes)
                        text = "{\"error\":\"Tool result exceeds the configured byte limit. Request smaller ranges. The operation ran and will not be replayed.\",\"omitted\":true}";
                    // Commit the result and cursor before observers or another provider request.
                    task.Messages.Add(new(AgentMessageKind.ToolResult, text, call.Id)); task.NextTool++;
                    Publish(task, "tool_completed", text, call.Id);
                }
                task.PendingReply = null; task.NextTool = 0;
            }
        }
        catch (OperationCanceledException) { task.Status = AgentTaskStatus.Cancelled; task.StatusReason = "Stopped, revoked or lease expired."; Publish(task, "cancelled", task.StatusReason); }
        catch (Exception error)
        { task.Status = AgentTaskStatus.Failed; task.StatusReason = error.Message; Publish(task, "failed", error.Message); throw; }
        finally
        {
            if (workspace != null && task.BeforeRun != null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var after = await CaptureWorkspaceAsync(deadline.Token);
                    task.Changes = new(after.Revision, Diff(task.BeforeRun.Documents, after.Documents));
                    Publish(task, "changes", $"{task.Changes.Files.Count} source files changed.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { Publish(task, "changes_unavailable", "The source checkpoint could not be refreshed. Inspect the current project before restoring."); }
            }
            _activeLease = null; _runGate.Release();
        }
    }

    private AutomationCatalog LocalTools(AgentTask task, Func<AgentQuestion, CancellationToken, Task<string>>? askUser, CancellationToken token)
    {
        var catalog = new AutomationCatalog();
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
    { if (task.Status is AgentTaskStatus.Running or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingAnswer) throw new InvalidOperationException("This task is running."); }
    private void Pause(AgentTask task, string reason) { task.Status = AgentTaskStatus.Paused; task.StatusReason = reason; Publish(task, "paused", reason); }
    private void Publish(AgentTask task, string kind, string text, string? callId = null)
    {
        if (text.Length > 262144) text = text[..262144] + "\n[public text truncated]";
        var item = new AgentEvent(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, task.Id, kind, text, callId);
        lock (task.Sync)
        {
        task.PublicEvents.Add(item);
        while (task.PublicEvents.Count > 1200 || task.PublicEvents.Sum(e => (long)e.Text.Length) > 4_000_000)
            task.PublicEvents.RemoveAt(0);
        }
        // UI/transport observers cannot interrupt a committed tool operation or strand a run.
        if (EventPublished is { } published)
            foreach (Action<AgentEvent> observer in published.GetInvocationList())
                try { observer(item); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
    public sealed record PlanArguments(long ExpectedRevision, AgentPlanStep[] Steps);
}
