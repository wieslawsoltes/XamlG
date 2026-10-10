using System.Text.Json;
using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentWorkflowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AgentRunOptions Options => new() { AutomaticCompaction = false, Policy = new() { Profile = PermissionProfile.AutoEdit } };

    [Fact]
    public async Task Queue_drains_in_edited_order_without_replenishing_the_run_budget()
    {
        var provider = new ScriptedAgentProvider(); var entered = Signal(); var release = Signal();
        provider.Add(async (_, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return ScriptedAgentProvider.Done("First finished"); });
        provider.Add(ScriptedAgentProvider.Done("Second finished")); provider.Add(ScriptedAgentProvider.Done("Third finished"));
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Queue", provider, "fixture", Token);
        var run = harness.RunAsync(task.Id, "First", Options with { Limits = new() { RequestsPerRun = 1 } }, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        harness.QueueMessage(task.Id, "Third"); harness.QueueMessage(task.Id, "Second draft");
        var queued = task.Queue;
        harness.EditQueuedMessage(task.Id, queued.Messages[1].Id, "Second", queued.Revision);
        harness.MoveQueuedMessage(task.Id, queued.Messages[1].Id, 0, task.Queue.Revision);
        release.SetResult(); await run;
        Assert.Single(provider.Requests); Assert.Equal(AgentTaskStatus.Paused, task.Status);
        Assert.Equal("Third", Assert.Single(task.Queue.Messages).Text);
        await harness.RunAsync(task.Id, null, Options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Empty(task.Queue.Messages);
        Assert.Equal(["First", "Second", "Third"], provider.Requests[^1].Messages.Where(message => message.Kind == AgentMessageKind.User).Select(message => message.Text));
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task Steering_skips_unstarted_tools_and_preserves_completed_results()
    {
        var provider = new ScriptedAgentProvider(); var effects = new List<int>();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        provider.Add(ScriptedAgentProvider.Done("Following the correction"));
        using var harness = new AgentHarness(Host(effects));
        var task = harness.CreateTask("Steer", provider, "fixture", Token);
        harness.EventPublished += item =>
        {
            if (item.Kind == "tool_completed" && item.ToolCallId == "one")
                harness.QueueMessage(task.Id, "Only make the first edit", AgentMessageDelivery.Steer);
        };
        await harness.RunAsync(task.Id, "Make both edits", Options, cancellationToken: Token);
        Assert.Equal([1], effects); Assert.Empty(task.Queue.Messages);
        var request = provider.Requests[1];
        Assert.Equal(["one", "two"], request.Messages.Where(message => message.Kind == AgentMessageKind.ToolResult).Select(message => message.ToolCallId));
        Assert.Contains("superseded", request.Messages.Single(message => message.ToolCallId == "two").Text);
        Assert.Equal("Only make the first edit", request.Messages[^1].Text);
        Assert.Single(task.Events, item => item.Kind == "tool_skipped");
    }

    [Fact]
    public async Task Stop_during_an_effect_preserves_uncertainty_and_resume_does_not_replay_it()
    {
        var provider = new ScriptedAgentProvider(); var effects = new List<int>();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        provider.Add(ScriptedAgentProvider.Done("Inspected the first edit"));
        AgentHarness? harness = null;
        var host = new AutomationCatalog();
        host.Add<EditArgs, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit, (args, context) =>
        { effects.Add(args.Value); harness!.Stop(); context.CancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<object>(new { }); });
        using (harness = new(host))
        {
            var task = harness.CreateTask("Resume", provider, "fixture", Token);
            await harness.RunAsync(task.Id, "Edit twice", Options, cancellationToken: Token);
            Assert.Equal(AgentTaskStatus.Paused, task.Status);
            await harness.RunAsync(task.Id, null, Options, cancellationToken: Token);
            Assert.Equal([1], effects); Assert.Equal(AgentTaskStatus.Completed, task.Status);
            var results = provider.Requests[1].Messages.Where(message => message.Kind == AgentMessageKind.ToolResult).ToArray();
            Assert.Equal(2, results.Length); Assert.Contains("effects may have occurred", results[0].Text);
            Assert.Contains("before this operation ran", results[1].Text);
            Assert.Single(provider.Requests[1].Messages, message => message.Kind == AgentMessageKind.User);
        }
    }

    [Fact]
    public async Task Plan_mode_filters_effects_even_with_full_access_and_exact_allow_rules()
    {
        var effects = new List<int>(); var provider = new ScriptedAgentProvider(); var host = Host(effects);
        host.Add<NoArgs, object>("inspect", "Inspect", AutomationScope.Source, AutomationEffect.Read, (_, _) => ValueTask.FromResult<object>(new { }));
        provider.Add((request, _, _) =>
        {
            Assert.Contains("Plan mode", request.Instructions);
            Assert.Contains(request.Tools, tool => tool.Name == "inspect");
            Assert.DoesNotContain(request.Tools, tool => tool.Name == "edit");
            return Task.FromResult(ScriptedAgentProvider.Call(Edit("guessed", 1)));
        });
        using var harness = new AgentHarness(host); var task = harness.CreateTask("Plan", provider, "fixture", Token);
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Plan a change", Options with
        {
            Mode = AgentCollaborationMode.Plan, FullAccessAcknowledged = true,
            Policy = new() { Profile = PermissionProfile.FullAccess, Tools = new Dictionary<string, PermissionDecision> { ["edit"] = PermissionDecision.Allow } }
        }, cancellationToken: Token));
        Assert.Empty(effects);
    }

    [Fact]
    public async Task Resuming_in_plan_mode_retires_an_unstarted_edit_batch_without_replaying_it()
    {
        var provider = new ScriptedAgentProvider(); var effects = new List<int>();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        provider.Add(ScriptedAgentProvider.Done("Inspect the source, then review the proposed edit."));
        using var harness = new AgentHarness(Host(effects)); var task = harness.CreateTask("Change mode", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Prepare both edits", Options with { Limits = new() { ToolsPerRun = 1 } }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Empty(effects);
        harness.SetMode(task.Id, AgentCollaborationMode.Plan);
        await harness.RunAsync(task.Id, null, Options with { Mode = AgentCollaborationMode.Plan }, cancellationToken: Token);
        Assert.Empty(effects); Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.NotNull(task.ProposedPlan);
        var results = provider.Requests[1].Messages.Where(message => message.Kind == AgentMessageKind.ToolResult).ToArray();
        Assert.Equal(["one", "two"], results.Select(result => result.ToolCallId));
        Assert.All(results, result => Assert.Contains("Plan mode superseded", result.Text));
        Assert.DoesNotContain(provider.Requests[1].Tools, tool => tool.Name == "edit");
    }

    [Fact]
    public async Task Proposed_plan_is_revision_checked_and_accepted_only_after_successful_preflight()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done("1. Inspect View.axaml\n2. Implement keyboard navigation\n3. Verify focus order"));
        provider.Add(ScriptedAgentProvider.Done("Implemented and verified"));
        var workspace = new AgentTestWorkspace(); using var harness = new AgentHarness(new AutomationCatalog(), workspace);
        var task = harness.CreateTask("Plan", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Plan keyboard navigation", Options with { Mode = AgentCollaborationMode.Plan }, cancellationToken: Token);
        var proposal = Assert.IsType<AgentPlanProposal>(task.ProposedPlan);
        Assert.False(proposal.Accepted); Assert.Equal(AgentCollaborationMode.Plan, task.Mode);
        await Assert.ThrowsAsync<AutomationException>(() => harness.ImplementPlanAsync(task.Id, proposal.Revision + 1, Options, cancellationToken: Token));
        workspace.BeforeCapture = _ => Task.FromException(new InvalidOperationException("Capture unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ImplementPlanAsync(task.Id, proposal.Revision, Options, cancellationToken: Token));
        Assert.False(task.ProposedPlan!.Accepted); Assert.Single(provider.Requests);
        workspace.BeforeCapture = null;
        await harness.ImplementPlanAsync(task.Id, proposal.Revision, Options, cancellationToken: Token);
        Assert.True(task.ProposedPlan!.Accepted); Assert.Equal(AgentCollaborationMode.Default, task.Mode);
        Assert.Contains(proposal.Markdown, provider.Requests[1].Messages[^1].Text);
        await Assert.ThrowsAsync<AutomationException>(() => harness.ImplementPlanAsync(task.Id, proposal.Revision, Options, cancellationToken: Token));
    }

    [Fact]
    public async Task Goal_continues_after_tool_progress_then_suppresses_a_no_progress_loop()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Call(Edit("one", 1)));
        provider.Add(ScriptedAgentProvider.Done("First improvement")); provider.Add(ScriptedAgentProvider.Done("No more evidence"));
        using var harness = new AgentHarness(Host([])); var task = harness.CreateTask("Goal", provider, "fixture", Token);
        harness.SetGoal(task.Id, "Improve and verify every keyboard interaction");
        await harness.RunAsync(task.Id, "Begin", Options, cancellationToken: Token);
        Assert.Equal(3, provider.Requests.Count); Assert.Equal(1, task.ActiveGoal!.Continuations);
        Assert.Contains("Improve and verify every keyboard interaction", provider.Requests[2].Instructions);
        Assert.Equal(AgentGoalStatus.Paused, task.ActiveGoal.Status);
        Assert.Equal(45, task.ActiveGoal.TokensUsed); Assert.Contains("no tool progress", task.ActiveGoal.Evidence);
        harness.ResumeGoal(task.Id); Assert.Equal(AgentGoalStatus.Active, task.ActiveGoal.Status);
    }

    [Fact]
    public async Task Goal_completion_retains_audit_evidence_and_does_not_trigger_another_turn()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(Goal("complete", AgentGoalStatus.Complete, "Verified all three keyboard cases in View.axaml with the focus test.")));
        provider.Add(ScriptedAgentProvider.Done("All requirements verified"));
        using var harness = new AgentHarness(new AutomationCatalog()); var task = harness.CreateTask("Goal", provider, "fixture", Token);
        harness.SetGoal(task.Id, "Verify keyboard interactions");
        await harness.RunAsync(task.Id, "Begin", Options, cancellationToken: Token);
        Assert.Equal(AgentGoalStatus.Complete, task.ActiveGoal!.Status); Assert.Contains("all three", task.ActiveGoal.Evidence);
        Assert.Equal(2, provider.Requests.Count); Assert.Equal(0, task.ActiveGoal.Continuations);
    }

    [Fact]
    public async Task Repeated_blocker_requires_three_separate_turns_and_resumption_resets_the_audit()
    {
        var provider = new ScriptedAgentProvider();
        for (var i = 0; i < 3; i++)
        {
            provider.Add(ScriptedAgentProvider.Call(Goal("blocked" + i, AgentGoalStatus.Blocked, "The required input file is unavailable.")));
            provider.Add(ScriptedAgentProvider.Done("Input is still missing"));
        }
        using var harness = new AgentHarness(new AutomationCatalog()); var task = harness.CreateTask("Blocked", provider, "fixture", Token);
        harness.SetGoal(task.Id, "Verify the supplied file");
        await harness.RunAsync(task.Id, "Begin", Options, cancellationToken: Token);
        Assert.Equal(6, provider.Requests.Count); Assert.Equal(AgentGoalStatus.Blocked, task.ActiveGoal!.Status);
        Assert.Equal(3, task.ActiveGoal.BlockedTurns);
        harness.ResumeGoal(task.Id); Assert.Equal(0, task.ActiveGoal.BlockedTurns);
    }

    [Fact]
    public async Task Plan_mode_never_automatically_continues_an_active_goal()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done("The implementation plan"));
        using var harness = new AgentHarness(new AutomationCatalog()); var task = harness.CreateTask("Plan goal", provider, "fixture", Token);
        harness.SetGoal(task.Id, "Finish the feature");
        await harness.RunAsync(task.Id, "Plan first", Options with { Mode = AgentCollaborationMode.Plan }, cancellationToken: Token);
        Assert.Single(provider.Requests); Assert.Equal(0, task.ActiveGoal!.Continuations); Assert.Equal(AgentGoalStatus.Active, task.ActiveGoal.Status);
        Assert.NotNull(task.ProposedPlan);
    }

    [Fact]
    public async Task Goal_budget_limits_each_request_and_survives_private_session_restore()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Call(Edit("one", 1)));
        using var harness = new AgentHarness(Host([])); var task = harness.CreateTask("Budget", provider, "fixture", Token);
        harness.SetGoal(task.Id, "Finish and verify the feature", tokenBudget: 30);
        await harness.RunAsync(task.Id, "Begin", Options, cancellationToken: Token);
        Assert.Equal(5, Assert.Single(provider.Requests).MaxOutputTokens);
        Assert.Equal(AgentGoalStatus.BudgetLimited, task.ActiveGoal!.Status); Assert.Equal(15, task.ActiveGoal.TokensUsed);
        using var restored = new AgentHarness(Host([]));
        var snapshot = JsonSerializer.Deserialize<AgentSessionSnapshot>(JsonSerializer.Serialize(harness.CaptureSession(), AutomationJson.Options), AutomationJson.Options)!;
        restored.RestoreSession(snapshot, (_, _) => provider, Token);
        Assert.Equal(task.ActiveGoal, restored.GetTask(task.Id).ActiveGoal);
        Assert.Throws<InvalidOperationException>(() => restored.ResumeGoal(task.Id, 15));
        restored.ResumeGoal(task.Id, 100); Assert.Equal(100, restored.GetTask(task.Id).ActiveGoal!.TokenBudget);
    }

    [Fact]
    public async Task Send_retries_are_idempotent_during_a_run_after_completion_and_after_reload()
    {
        var provider = new ScriptedAgentProvider(); var entered = Signal(); var release = Signal();
        provider.Add(async (_, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return ScriptedAgentProvider.Done(); });
        using var session = new AgentWorkbenchSession(new AutomationCatalog(), [provider]);
        var task = session.Harness.CreateTask("Send", provider, "fixture", Token);
        var args = AutomationJson.Element(new { id = task.Id, text = "Only once", clientMessageId = "submission-1", options = Options });
        await session.ExecuteAsync("send", args, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await session.ExecuteAsync("send", args, Token);
        release.SetResult(); await Idle(session);
        session.Harness.QueueMessage(task.Id, "An unrelated manually retained follow-up");
        await session.ExecuteAsync("send", args, Token); await Idle(session);
        Assert.Single(provider.Requests); Assert.Single(task.Events, item => item.Kind == "user");
        Assert.Single(task.Queue.Messages);
        using var restored = new AgentWorkbenchSession(new AutomationCatalog(), [provider]);
        restored.RestoreSession(session.Harness.CaptureSession(), Token);
        await restored.ExecuteAsync("send", args, Token); await Idle(restored);
        Assert.Single(provider.Requests);
        await Assert.ThrowsAsync<AutomationException>(() => restored.ExecuteAsync("send",
            AutomationJson.Element(new { id = task.Id, text = "Different content", clientMessageId = "submission-1", options = Options }), Token));
    }

    [Fact]
    public void Receipt_eviction_keeps_the_identity_of_messages_still_in_the_queue()
    {
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Long queue", new ScriptedAgentProvider(), "fixture", Token);
        harness.QueueMessage(task.Id, "Retain this follow-up", clientMessageId: "pending");
        for (var i = 0; i < 260; i++)
        {
            var id = "removed-" + i;
            harness.QueueMessage(task.Id, "Removed follow-up", clientMessageId: id);
            harness.RemoveQueuedMessage(task.Id, id, task.Queue.Revision);
        }
        harness.QueueMessage(task.Id, "Retain this follow-up", clientMessageId: "pending");
        Assert.Equal("pending", Assert.Single(task.Queue.Messages).Id);
        Assert.Equal(256, harness.CaptureSession().Tasks.Single().Submissions.Count);
    }

    [Fact]
    public async Task Failed_send_preflight_keeps_the_accepted_message_for_retry()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        var workspace = new AgentTestWorkspace { BeforeCapture = _ => Task.FromException(new InvalidOperationException("Capture unavailable")) };
        using var session = new AgentWorkbenchSession(new AutomationCatalog(), [provider], workspace);
        var task = session.Harness.CreateTask("Send", provider, "fixture", Token);
        var args = AutomationJson.Element(new { id = task.Id, text = "Retain this request", clientMessageId = "submission-1", options = Options });
        await session.ExecuteAsync("send", args, Token); await Idle(session);
        Assert.Single(task.Queue.Messages); Assert.Empty(provider.Requests); Assert.Equal(AgentTaskStatus.Ready, task.Status);
        workspace.BeforeCapture = null;
        await session.ExecuteAsync("send", args, Token); await Idle(session);
        Assert.Empty(task.Queue.Messages); Assert.Single(provider.Requests);
    }

    private static async Task Idle(AgentWorkbenchSession session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (session.IsRunning) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task Late_draft_writes_cannot_restore_sent_text_or_erase_new_typing()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        using var session = new AgentWorkbenchSession(new AutomationCatalog(), [provider]);
        var task = session.Harness.CreateTask("Draft", provider, "fixture", Token);
        await session.ExecuteAsync("draft", AutomationJson.Element(new { id = task.Id, text = "Sent text", revision = 1 }), Token);
        await session.ExecuteAsync("send", AutomationJson.Element(new { id = task.Id, text = "Sent text", clientMessageId = "one", draftRevision = 2, options = Options }), Token);
        await session.ExecuteAsync("draft", AutomationJson.Element(new { id = task.Id, text = "Sent text", revision = 1 }), Token);
        Assert.Equal("", task.Draft); Assert.Equal(2, task.DraftRevision);
        await session.ExecuteAsync("draft", AutomationJson.Element(new { id = task.Id, text = "Next draft", revision = 3 }), Token);
        await session.ExecuteAsync("send", AutomationJson.Element(new { id = task.Id, text = "Sent text", clientMessageId = "one", draftRevision = 2, options = Options }), Token);
        Assert.Equal("Next draft", task.Draft); Assert.Equal(3, task.DraftRevision);
        await Idle(session); Assert.Single(provider.Requests);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AgentToolCall Edit(string id, int value) => new(id, "edit", AutomationJson.Element(new EditArgs(value)));
    private static AgentToolCall Goal(string id, AgentGoalStatus status, string evidence) => new(id, "xamlg_agent_goal", AutomationJson.Element(new AgentHarness.GoalUpdateArguments(status, evidence)));
    private static AutomationCatalog Host(List<int> effects)
    {
        var host = new AutomationCatalog();
        host.Add<EditArgs, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit, (args, _) =>
        { effects.Add(args.Value); return ValueTask.FromResult<object>(new { revision = effects.Count }); });
        return host;
    }
    public sealed record EditArgs(int Value);
    public sealed record NoArgs;
}
