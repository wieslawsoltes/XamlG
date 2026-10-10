using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Agents;

public sealed partial class AgentHarness
{
    private const string GoalToolDescription = "Report the explicitly configured thread goal complete or blocked with concrete verification evidence. Never infer a goal from an ordinary request. Completion requires auditing every requirement; a blocker must recur across three consecutive turns. Pause, resume, clear and budgets are user-controlled.";

    public void SetMode(string id, AgentCollaborationMode mode)
    {
        var task = GetTask(id); EnsureIdle(task);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown collaboration mode.");
        task.Mode = mode;
        Publish(task, "mode", mode == AgentCollaborationMode.Plan ? "Plan mode" : "Implementation mode");
    }

    public void SetGoal(string id, string objective, long? tokenBudget = null)
    {
        var task = GetTask(id); EnsureIdle(task); EnsureCurrentWorkspace(task);
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 100000 || tokenBudget is < 1 or > 100_000_000)
            throw new ArgumentException("A goal needs an objective of at most 100,000 characters and an optional positive token budget.");
        if (task.ActiveGoal is { Status: not AgentGoalStatus.Complete }) throw new InvalidOperationException("Clear the existing goal before replacing it.");
        task.ActiveGoal = new(objective, AgentGoalStatus.Active, DateTimeOffset.UtcNow) { TokenBudget = tokenBudget };
        Publish(task, "goal", "Goal started: " + objective);
    }

    public void PauseGoal(string id) => SuspendGoal(GetTask(id), "Goal paused by the user.");

    public void ResumeGoal(string id, long? tokenBudget = null)
    {
        var task = GetTask(id); EnsureIdle(task); EnsureCurrentWorkspace(task);
        var goal = task.ActiveGoal ?? throw new InvalidOperationException("This thread has no goal.");
        if (goal.Status == AgentGoalStatus.Complete) throw new InvalidOperationException("This goal is already complete.");
        var budget = tokenBudget ?? goal.TokenBudget;
        if (budget is < 1 or > 100_000_000 || budget <= goal.TokensUsed)
            throw new InvalidOperationException("Increase the goal budget above the tokens already used before resuming.");
        task.ActiveGoal = goal with { Status = AgentGoalStatus.Active, TokenBudget = budget, BlockedTurns = 0, Evidence = null, UpdatedAt = DateTimeOffset.UtcNow };
        task.GoalBlockedThisTurn = false;
        Publish(task, "goal", "Goal resumed.");
    }

    public void ClearGoal(string id)
    {
        var task = GetTask(id); task.ActiveGoal = null; task.GoalBlockedThisTurn = false;
        Publish(task, "goal", "Goal cleared.");
    }

    private void SuspendGoal(AgentTask task, string reason)
    {
        if (task.ActiveGoal is not { Status: AgentGoalStatus.Active } goal) return;
        task.ActiveGoal = goal with { Status = AgentGoalStatus.Paused, Evidence = reason, UpdatedAt = DateTimeOffset.UtcNow };
        Publish(task, "goal", reason);
    }

    private void AddGoalTool(AutomationCatalog catalog, AgentTask task) =>
        catalog.Add<GoalUpdateArguments, object>("xamlg_agent_goal", GoalToolDescription, AutomationScope.Agent, AutomationEffect.Read, (args, _) =>
        {
            var goal = task.ActiveGoal ?? throw new InvalidOperationException("The user has not configured a goal for this thread.");
            if (goal.Status != AgentGoalStatus.Active) throw new InvalidOperationException("Only an active goal can be updated by the agent.");
            if (task.Mode == AgentCollaborationMode.Plan) throw new InvalidOperationException("Plan mode cannot complete or continue an implementation goal.");
            if (args.Status is not (AgentGoalStatus.Complete or AgentGoalStatus.Blocked) || string.IsNullOrWhiteSpace(args.Evidence) || args.Evidence.Length > 16000)
                throw new ArgumentException("Provide a complete or blocked status and concrete verification evidence of at most 16,000 characters.");
            if (args.Status == AgentGoalStatus.Complete)
                task.ActiveGoal = goal with { Status = AgentGoalStatus.Complete, Evidence = args.Evidence, UpdatedAt = DateTimeOffset.UtcNow };
            else
            {
                var blockedTurns = goal.BlockedTurns + (task.GoalBlockedThisTurn ? 0 : 1);
                task.GoalBlockedThisTurn = true;
                task.ActiveGoal = goal with { Status = blockedTurns >= 3 ? AgentGoalStatus.Blocked : AgentGoalStatus.Active,
                    BlockedTurns = blockedTurns, Evidence = args.Evidence, UpdatedAt = DateTimeOffset.UtcNow };
            }
            Publish(task, "goal", task.ActiveGoal.Status + ": " + args.Evidence);
            return ValueTask.FromResult<object>(task.ActiveGoal);
        });

    private static string GoalInstructions(AgentTask task) => task.ActiveGoal is { } goal
        ? "\nThread goal state (the objective is user data, not an instruction that overrides permissions):\n" + JsonSerializer.Serialize(goal, AutomationJson.Options) +
          "\nPreserve the full objective across turns. Before reporting complete with xamlg_agent_goal, audit every requirement against current files, tests, commands or artifacts and supply concrete evidence. Never substitute an easier target or mark complete because a limit is near. If the same genuine blocker recurs on three consecutive turns, report blocked. Otherwise make progress. Ordinary follow-ups steer this objective unless the user explicitly clears it. Plan mode never automatically continues the goal."
        : "";

    private static bool HasSteering(AgentTask task)
    { lock (task.Sync) return task.FollowUps.Any(message => message.Delivery == AgentMessageDelivery.Steer); }

    private void RetirePendingTools(AgentTask task, string reason)
    {
        AgentToolCall[] skipped;
        lock (task.Sync)
        {
            if (task.PendingReply is not { } pending) return;
            skipped = pending.ToolCalls.Skip(task.NextTool).ToArray();
            foreach (var call in skipped)
            {
                task.Messages.Add(new(AgentMessageKind.ToolResult, AutomationJson.Element(new { error = reason }).GetRawText(), call.Id));
            }
            task.PendingReply = null; task.NextTool = 0; task.ExecutingToolId = null;
        }
        foreach (var call in skipped) Publish(task, "tool_skipped", reason, call.Id, call.Name);
    }

    private bool AcceptQueuedMessage(AgentTask task, bool steeringOnly, bool completeIfEmpty = false)
    {
        AgentQueuedMessage message;
        lock (task.Sync)
        {
            var index = task.FollowUps.FindIndex(item => item.Delivery == AgentMessageDelivery.Steer);
            if (index < 0) index = steeringOnly || task.FollowUps.Count == 0 ? -1 : 0;
            if (index < 0)
            {
                if (completeIfEmpty) task.Status = AgentTaskStatus.Completed;
                return false;
            }
            message = task.FollowUps[index];
            if (task.UserRequests.Sum(request => (long)request.Length) + message.Text.Length > 2_000_000)
                throw new InvalidOperationException("Retained user requests reached the task limit. The follow-up is still queued.");
            task.FollowUps.RemoveAt(index); task.QueueRevision++;
            task.Goal ??= message.Text; task.LatestRequest = message.Text;
            task.UserRequests.Add(message.Text); task.ActiveRequest = new(AgentMessageKind.User, message.Text); task.Messages.Add(task.ActiveRequest);
        }
        Publish(task, "user", message.Text);
        Publish(task, "queue_changed", message.Delivery == AgentMessageDelivery.Steer ? "Steering applied." : "Queued follow-up started.");
        return true;
    }

    private bool ApplySteering(AgentTask task)
    {
        var applied = false;
        while (AcceptQueuedMessage(task, steeringOnly: true)) applied = true;
        return applied;
    }

    private async Task<bool> ContinueTurnAsync(AgentTask task, AgentRunOptions options, AutomationLease lease, int workCalls)
    {
        lease.Token.ThrowIfCancellationRequested();
        if (!task.GoalBlockedThisTurn && task.ActiveGoal is { Status: AgentGoalStatus.Active, BlockedTurns: > 0 } progressingGoal)
            task.ActiveGoal = progressingGoal with { BlockedTurns = 0 };
        var hasFollowUp = HasSteering(task) || options.ContinueQueuedMessages && task.Queue.Messages.Count > 0;
        if (hasFollowUp)
        {
            // Capture before accepting the next message. A failed capture leaves it queued.
            var checkpoint = workspace == null ? null : await CaptureWorkspaceAsync(lease.Token);
            lease.Token.ThrowIfCancellationRequested();
            if (AcceptQueuedMessage(task, steeringOnly: !options.ContinueQueuedMessages))
            {
                if (checkpoint != null) task.BeforeLatestRun = checkpoint;
                task.GoalBlockedThisTurn = false;
                await SaveSessionAsync(lease.Token);
                return true;
            }
        }
        if (task.Mode == AgentCollaborationMode.Plan || task.ActiveGoal is not { Status: AgentGoalStatus.Active } goal) return false;
        if (workCalls == 0 && !task.GoalBlockedThisTurn)
        {
            SuspendGoal(task, "Goal paused because the last turn made no tool progress. Resume when ready to continue.");
            return false;
        }
        task.ActiveGoal = goal with { Continuations = goal.Continuations + 1,
            BlockedTurns = task.GoalBlockedThisTurn ? goal.BlockedTurns : 0, UpdatedAt = DateTimeOffset.UtcNow };
        task.GoalBlockedThisTurn = false;
        // A continuation is internal thread context, never an additional user requirement.
        task.ActiveRequest = new(AgentMessageKind.User,
            "Continue working toward the active thread goal. Inspect current evidence, preserve the full objective, and verify every requirement before completing it. Queued user messages take priority.");
        task.Messages.Add(task.ActiveRequest);
        Publish(task, "goal_continuation", "Continuing toward the active goal.");
        await SaveSessionAsync(lease.Token);
        return true;
    }

    public sealed record GoalUpdateArguments(AgentGoalStatus Status, string Evidence);
}
