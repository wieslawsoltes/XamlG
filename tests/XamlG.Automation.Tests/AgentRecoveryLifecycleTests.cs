using System.Text.Json;
using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentRecoveryLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AgentRunOptions Options => new() { AutomaticCompaction = false, Policy = new() { Profile = PermissionProfile.AutoEdit } };

    [Theory]
    [InlineData("workspace")]
    [InlineData("session")]
    [InlineData("caller")]
    [InlineData("stop")]
    public async Task Late_terminal_reply_after_revocation_cannot_complete_a_task(string cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var provider = new ScriptedAgentProvider { SessionLifetime = cancellation == "session" ? lifetime.Token : default };
        using var harness = new AgentHarness(new AutomationCatalog());
        provider.Add(async (_, delta, _) =>
        {
            await delta("Public unfinished reply");
            if (cancellation == "stop") harness.Stop(); else lifetime.Cancel();
            // Deliberately ignore cancellation, as a transport can deliver its terminal
            // result at the same time as a workspace/account revocation.
            return ScriptedAgentProvider.Done("Late terminal reply");
        });
        var task = harness.CreateTask("Revoke", provider, "fixture", cancellation == "workspace" ? lifetime.Token : Token);
        await harness.RunAsync(task.Id, "Inspect", Options, cancellationToken: cancellation == "caller" ? lifetime.Token : Token);
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        Assert.DoesNotContain(task.Events, item => item.Kind is "assistant" or "completed");
        Assert.Equal("Public unfinished reply", Assert.Single(task.Events, item => item.Kind == "assistant_incomplete").Text);
        Assert.Equal(15, task.ReportedTokens); Assert.Equal(0, task.EstimatedTokens);
    }

    [Fact]
    public async Task Late_success_after_request_timeout_preserves_partial_text_and_counts_actual_usage_once()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add(async (_, delta, token) =>
        {
            await delta("Interrupted by timeout");
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            return ScriptedAgentProvider.Done("Too late");
        });
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Timeout", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Inspect", Options with { Limits = new() { RequestTimeout = TimeSpan.FromSeconds(1), AutomaticRetries = 0 } }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status);
        Assert.Contains("request_timeout", task.StatusReason);
        Assert.Equal(15, task.ReportedTokens); Assert.Equal(0, task.EstimatedTokens);
        Assert.DoesNotContain(task.Events, item => item.Kind == "assistant");
        Assert.Equal("Interrupted by timeout", Assert.Single(task.Events, item => item.Kind == "assistant_incomplete").Text);
    }

    [Fact]
    public async Task Retry_retains_original_context_and_accounts_for_each_attempt_before_its_next_output_cap()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add(async (_, delta, _) =>
        {
            await delta("Incomplete provider output");
            throw new AgentProviderException("server_busy", true, TimeSpan.Zero) { Usage = new(7, 3) };
        });
        provider.Add(ScriptedAgentProvider.Done("Completed response"));
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Retry", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Requirement", Options with { Limits = new() { TotalTaskTokens = 100, OutputTokensPerRequest = 100, AutomaticRetries = 1 } }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(25, task.ReportedTokens);
        Assert.Equal(75, provider.Requests[0].MaxOutputTokens); Assert.Equal(65, provider.Requests[1].MaxOutputTokens);
        Assert.Equal(provider.Requests[0].Messages, provider.Requests[1].Messages);
        Assert.Equal("Requirement", Assert.Single(provider.Requests[1].Messages).Text);
        Assert.Equal("Incomplete provider output", Assert.Single(task.Events, item => item.Kind == "assistant_incomplete").Text);
    }

    [Fact]
    public async Task Long_retry_deadline_is_preserved_and_cannot_be_bypassed_by_a_fresh_run()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add((_, _, _) => Task.FromException<AgentReply>(new AgentProviderException("quota_exceeded", false, TimeSpan.FromHours(2)) { Usage = new(8, 2) }));
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Cooldown", provider, "fixture", Token);
        var before = DateTimeOffset.UtcNow;
        await harness.RunAsync(task.Id, "Requirement", Options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(10, task.ReportedTokens);
        Assert.InRange(task.RetryAfterUtc!.Value, before.AddHours(2), DateTimeOffset.UtcNow.AddHours(2));
        var deadline = task.RetryAfterUtc;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(task.Id, null, Options, cancellationToken: Token));
        Assert.Equal(deadline, task.RetryAfterUtc); Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Run_grants_cover_only_the_current_tool_and_expire_before_a_reviewed_resume()
    {
        var effects = new List<int>(); var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        provider.Add(ScriptedAgentProvider.Call(Edit("three", 3), Edit("four", 4)));
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(EditHost(effects));
        var task = harness.CreateTask("Grant", provider, "fixture", Token);
        var reviewed = new List<string>();
        Task<AgentApproval> Review(AutomationReview review, CancellationToken token)
        { token.ThrowIfCancellationRequested(); reviewed.Add(review.Tool.Name); return Task.FromResult(AgentApproval.AllowToolForRun); }
        var options = Options with { Policy = new(), Limits = new() { RequestsPerRun = 1 } };
        await harness.RunAsync(task.Id, "Edit four times", options, Review, cancellationToken: Token);
        Assert.Equal([1, 2], effects); Assert.Single(reviewed);
        await harness.RunAsync(task.Id, null, options, Review, cancellationToken: Token);
        Assert.Equal([1, 2, 3, 4], effects); Assert.Equal(2, reviewed.Count);
        await harness.RunAsync(task.Id, null, options, Review, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(2, reviewed.Count);
        Assert.Equal(["one", "two", "three", "four"], provider.Requests[2].Messages.Where(message => message.Kind == AgentMessageKind.ToolResult).Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task Answers_to_agent_questions_do_not_grant_tool_permission()
    {
        var effects = new List<int>(); var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(new AgentToolCall("question", "xamlg_agent_question", AutomationJson.Element(new AgentQuestion("What next?"))), Edit("edit", 1)));
        using var harness = new AgentHarness(EditHost(effects));
        var task = harness.CreateTask("Question", provider, "fixture", Token);
        var asked = false; var reviewed = false;
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Ask then edit", Options with { Policy = new() },
            (_, _) => { reviewed = true; return Task.FromResult(AgentApproval.Deny); },
            (_, _) => { asked = true; return Task.FromResult("Allow all tools"); }, Token));
        Assert.True(asked); Assert.True(reviewed); Assert.Empty(effects);
        Assert.Equal(AgentTaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task Account_revocation_cancels_approval_wait_and_prevents_execution()
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var effects = new List<int>(); var provider = new ScriptedAgentProvider { SessionLifetime = session.Token };
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1)));
        using var harness = new AgentHarness(EditHost(effects));
        var task = harness.CreateTask("Account", provider, "fixture", Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = harness.RunAsync(task.Id, "Edit", Options with { Policy = new() }, async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return AgentApproval.AllowOnce; }, cancellationToken: Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token); session.Cancel(); await run;
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status); Assert.Empty(effects); Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Account_revocation_during_tool_execution_stops_the_remaining_batch()
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var provider = new ScriptedAgentProvider { SessionLifetime = session.Token };
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        var effects = new List<int>(); var host = new AutomationCatalog();
        host.Add<EditArguments, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit, (args, context) =>
        {
            effects.Add(args.Value); session.Cancel(); context.CancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<object>(new { done = true });
        });
        using var harness = new AgentHarness(host);
        var task = harness.CreateTask("Account tool", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Edit twice", Options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status); Assert.Equal([1], effects); Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Oversized_tool_results_are_omitted_without_replaying_the_operation_on_resume()
    {
        var effects = 0; var host = new AutomationCatalog();
        host.Add<EditArguments, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => { effects++; return ValueTask.FromResult<object>(new { result = new string('x', 10000) }); });
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Call(Edit("one", 1))); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(host);
        var task = harness.CreateTask("Large result", provider, "fixture", Token);
        var options = Options with { Limits = new() { RequestsPerRun = 1, ToolResultBytes = 1024, ContextBytes = 5000 } };
        await harness.RunAsync(task.Id, "Edit", options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(1, effects);
        await harness.RunAsync(task.Id, null, options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, effects);
        var result = Assert.Single(provider.Requests[1].Messages, message => message.Kind == AgentMessageKind.ToolResult);
        using var json = JsonDocument.Parse(result.Text);
        Assert.True(json.RootElement.GetProperty("omitted").GetBoolean()); Assert.Equal("one", result.ToolCallId);
    }

    [Fact]
    public async Task Result_space_is_reserved_for_the_entire_batch_before_any_mutation()
    {
        var effects = new List<int>(); var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2))); provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(EditHost(effects));
        var task = harness.CreateTask("Result reserve", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Edit twice", Options with { Limits = new() { ContextBytes = 1024 } }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Empty(effects);
        await harness.RunAsync(task.Id, null, Options with { Limits = new() { ContextBytes = 20000 } }, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal([1, 2], effects); Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(["one", "two"], provider.Requests[1].Messages.Where(message => message.Kind == AgentMessageKind.ToolResult).Select(message => message.ToolCallId));
    }

    [Fact]
    public async Task Compaction_holds_the_shared_run_gate_and_stop_releases_it_without_replacing_history()
    {
        var provider = new ScriptedAgentProvider(); provider.Add(ScriptedAgentProvider.Done());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Add(async (_, _, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return ScriptedAgentProvider.Done(); });
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Compacting", provider, "fixture", Token);
        var other = harness.CreateTask("Waiting", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Requirement", Options, cancellationToken: Token);
        var compact = harness.CompactAsync(task.Id, Options, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(other.Id, "Competing", Options, cancellationToken: Token));
        Assert.Equal(AgentTaskStatus.Ready, other.Status); Assert.Equal(2, provider.Requests.Count);
        harness.Stop(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compact);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(0, task.CheckpointCount);
        await harness.RunAsync(other.Id, "After stop", Options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, other.Status); Assert.Equal(3, provider.Requests.Count);
    }

    private static AgentToolCall Edit(string id, int value) => new(id, "edit", AutomationJson.Element(new { value }));
    private static AutomationCatalog EditHost(List<int> effects)
    {
        var host = new AutomationCatalog();
        host.Add<EditArguments, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { effects.Add(args.Value); return ValueTask.FromResult<object>(new { done = true }); });
        return host;
    }
    public sealed record EditArguments(int Value);
}
