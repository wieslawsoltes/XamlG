using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentCompactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AgentRunOptions Options => new() { AutomaticCompaction = false };

    [Fact]
    public async Task Checkpoint_is_tool_free_preserves_requirements_plan_and_complete_native_turns()
    {
        var oldNative = new object(); var recentNative = new object(); var summaryNative = new object();
        var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(new AgentToolCall("plan", "xamlg_agent_plan", AutomationJson.Element(new
        {
            expectedRevision = 0,
            steps = new[] { new AgentPlanStep("inspect", "Inspect the current source", AgentStepStatus.InProgress) }
        }))));
        provider.Add(ScriptedAgentProvider.Done("First reply", oldNative));
        provider.Add(ScriptedAgentProvider.Done("Second reply", recentNative));
        provider.Add(ScriptedAgentProvider.Done("Public summary", summaryNative));
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Compact", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Keep keyboard access", Options, cancellationToken: Token);
        await harness.RunAsync(task.Id, "Keep CRLF source", Options, cancellationToken: Token);
        harness.QueueMessage(task.Id, "Private unsent draft");
        Assert.True(await harness.CompactAsync(task.Id, Options with { Compaction = new() { RecentCompleteTurns = 1 } }, Token));
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, task.CheckpointCount);
        var summary = provider.Requests[3];
        Assert.Empty(summary.Tools); Assert.All(summary.Messages, message => Assert.Null(message.Native));
        Assert.Contains("Keep keyboard access", summary.Messages[0].Text);
        Assert.Contains("Keep CRLF source", summary.Messages[0].Text);
        Assert.Contains("Inspect the current source", summary.Messages[0].Text);
        Assert.DoesNotContain("Private unsent draft", summary.Messages[0].Text);
        await harness.RunAsync(task.Id, "Continue", Options, cancellationToken: Token);
        var next = provider.Requests[4];
        Assert.Equal(4, next.Messages.Count);
        Assert.Contains("Public summary", next.Messages[0].Text);
        Assert.Contains("Keep keyboard access", next.Messages[0].Text);
        Assert.Contains("planRevision", next.Messages[0].Text);
        Assert.Equal("Keep CRLF source", next.Messages[1].Text);
        Assert.Same(recentNative, next.Messages[2].Native);
        Assert.DoesNotContain(next.Messages, message => ReferenceEquals(message.Native, oldNative) || ReferenceEquals(message.Native, summaryNative));
        Assert.Single(task.Queue.Messages);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("tools")]
    [InlineData("oversized")]
    [InlineData("output_limit")]
    [InlineData("cannot_fit")]
    [InlineData("transport")]
    public async Task Failed_checkpoint_preserves_native_history_and_counts_the_attempt(string failure)
    {
        var native = new object(); var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Done("Retained answer", native));
        provider.Add((_, _, _) => failure switch
        {
            "blank" => Task.FromResult(ScriptedAgentProvider.Done(" ")),
            "tools" => Task.FromResult(ScriptedAgentProvider.Call(new AgentToolCall("bad", "not_available", AutomationJson.Element(new { })))),
            "oversized" => Task.FromResult(ScriptedAgentProvider.Done(new string('x', 1_000_001))),
            "output_limit" => Task.FromResult(ScriptedAgentProvider.Done() with { OutputLimitReached = true }),
            "cannot_fit" => Task.FromResult(ScriptedAgentProvider.Done("Too large checkpoint")),
            _ => Task.FromException<AgentReply>(new AgentProviderException("unavailable", false, TimeSpan.Zero) { Usage = new(7, 3) })
        });
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Keep context", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Requirement", Options, cancellationToken: Token);
        if (failure == "cannot_fit")
            provider.ContextSize = request => request.Messages.Any(message => message.Text.StartsWith("Continue from this public checkpoint", StringComparison.Ordinal)) ? 2000 : 100;
        var compact = harness.CompactAsync(task.Id, Options with { Limits = new() { ContextBytes = 1024 } }, Token);
        if (failure is "blank" or "tools" or "oversized") await Assert.ThrowsAsync<InvalidOperationException>(() => compact);
        else Assert.False(await compact);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(0, task.CheckpointCount);
        Assert.Equal(failure == "transport" ? 25 : 30, task.ReportedTokens);
        await harness.RunAsync(task.Id, "Next requirement", Options, cancellationToken: Token);
        Assert.Equal(["Requirement", "Retained answer", "Next requirement"], provider.Requests[2].Messages.Select(message => message.Text));
        Assert.Same(native, provider.Requests[2].Messages[1].Native);
    }

    [Fact]
    public async Task Unexecuted_batch_can_be_regenerated_without_displacing_the_last_complete_turn()
    {
        var effects = new List<int>(); var host = EditHost(effects); var provider = new ScriptedAgentProvider();
        var completedNative = new object();
        provider.Add(ScriptedAgentProvider.Done("Completed first turn", completedNative));
        provider.Add(ScriptedAgentProvider.Call(Edit("deferred-one", 1), Edit("deferred-two", 2)));
        provider.Add(ScriptedAgentProvider.Done("No deferred edits have run"));
        provider.Add(ScriptedAgentProvider.Call(Edit("replacement", 3)));
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(host);
        var task = harness.CreateTask("Batch", provider, "fixture", Token);
        var options = Options with { Limits = new() { ToolsPerRun = 1 }, Policy = new() { Profile = PermissionProfile.AutoEdit }, Compaction = new() { RecentCompleteTurns = 1 } };
        await harness.RunAsync(task.Id, "First requirement", options, cancellationToken: Token);
        await harness.RunAsync(task.Id, "Second requirement", options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Empty(effects);
        Assert.True(await harness.CompactAsync(task.Id, options, Token));
        Assert.Equal(AgentTaskStatus.Paused, task.Status);
        await harness.RunAsync(task.Id, null, options, cancellationToken: Token);
        Assert.Equal([3], effects);
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        var resumed = provider.Requests[3];
        Assert.Contains(resumed.Messages, message => ReferenceEquals(message.Native, completedNative));
        Assert.Contains("Second requirement", resumed.Messages[0].Text);
        Assert.Equal("Second requirement", resumed.Messages[^1].Text);
        Assert.DoesNotContain(resumed.Messages, message => message.Kind == AgentMessageKind.ToolResult);
    }

    [Fact]
    public async Task Cancellation_after_one_tool_never_allows_compaction_of_the_remaining_partial_batch()
    {
        var effects = new List<int>(); var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(Edit("one", 1), Edit("two", 2)));
        using var harness = new AgentHarness(EditHost(effects));
        harness.EventPublished += item => { if (item.Kind == "tool_completed") harness.Stop(); };
        var task = harness.CreateTask("Partial", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Change twice", Options with { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: Token);
        Assert.Equal([1], effects); Assert.Equal(AgentTaskStatus.Cancelled, task.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CompactAsync(task.Id, Options, Token));
        Assert.Single(provider.Requests); Assert.Equal(0, task.CheckpointCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(task.Id, null, Options, cancellationToken: Token));
        Assert.Equal([1], effects);
    }

    [Fact]
    public async Task Automatic_checkpoint_retains_completed_turns_and_shares_the_run_request_budget()
    {
        var provider = new ScriptedAgentProvider(); var native = new object();
        provider.Add(ScriptedAgentProvider.Done("Complete first turn", native));
        provider.Add(ScriptedAgentProvider.Done("Checkpoint"));
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Automatic", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "First", Options, cancellationToken: Token);
        var options = Options with { AutomaticCompaction = true, Limits = new() { RequestsPerRun = 1 },
            Compaction = new() { AutomaticInputTokens = 1, RecentCompleteTurns = 1 } };
        await harness.RunAsync(task.Id, "Second", options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(1, task.CheckpointCount);
        Assert.Equal(2, provider.Requests.Count);
        await harness.RunAsync(task.Id, null, Options, cancellationToken: Token);
        Assert.Same(native, Assert.Single(provider.Requests[2].Messages, message => message.Kind == AgentMessageKind.Assistant).Native);
        Assert.Contains("Second", provider.Requests[2].Messages[0].Text);
    }

    [Fact]
    public async Task Model_window_trigger_operates_when_the_optional_input_threshold_is_disabled()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Done("Old native"));
        provider.Add(ScriptedAgentProvider.Done("Checkpoint"));
        provider.Add(ScriptedAgentProvider.Done());
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Window", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "First", Options, cancellationToken: Token);
        provider.ContextSize = request => request.Messages.Any(message => message.Native != null) ? 6000 : 100;
        var options = Options with { AutomaticCompaction = true, Limits = new() { OutputTokensPerRequest = 256 },
            Compaction = new() { AutomaticInputTokens = 0, ModelContextWindowTokens = 1000, RecentCompleteTurns = 0, CheckpointOutputTokens = 256 } };
        await harness.RunAsync(task.Id, "Second", options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, task.CheckpointCount);
        Assert.Equal(3, provider.Requests.Count); Assert.Empty(provider.Requests[1].Tools);
    }

    [Fact]
    public async Task Checkpoint_request_respects_the_model_window_before_contacting_the_provider()
    {
        var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Done());
        provider.Add(ScriptedAgentProvider.Done("Should not be requested"));
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Window", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Required user context", Options, cancellationToken: Token);
        provider.ContextSize = request => request.Tools.Count == 0 ? 4000 : 100;
        var options = Options with { Limits = new() { OutputTokensPerRequest = 256 },
            Compaction = new() { ModelContextWindowTokens = 1000, CheckpointOutputTokens = 256 } };
        Assert.False(await harness.CompactAsync(task.Id, options, Token));
        Assert.Single(provider.Requests); Assert.Equal(0, task.CheckpointCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_summary_cannot_publish_a_checkpoint_even_if_the_provider_returns(bool cancelSession)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var provider = new ScriptedAgentProvider { SessionLifetime = cancelSession ? lifetime.Token : default };
        var native = new object(); provider.Add(ScriptedAgentProvider.Done("Original", native));
        provider.Add((_, _, _) => { lifetime.Cancel(); return Task.FromResult(ScriptedAgentProvider.Done("Too late")); });
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Cancelled checkpoint", provider, "fixture", Token);
        await harness.RunAsync(task.Id, "Requirement", Options, cancellationToken: Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CompactAsync(task.Id, Options, cancelSession ? Token : lifetime.Token));
        Assert.Equal(0, task.CheckpointCount); Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.DoesNotContain(task.Events, item => item.Kind == "checkpoint");
        provider.SessionLifetime = default; provider.Add(ScriptedAgentProvider.Done());
        await harness.RunAsync(task.Id, "Continue", Options, cancellationToken: Token);
        Assert.Same(native, provider.Requests[2].Messages[1].Native);
    }

    private static AgentToolCall Edit(string id, int value) => new(id, "edit", AutomationJson.Element(new { value }));
    private static AutomationCatalog EditHost(List<int> effects)
    {
        var catalog = new AutomationCatalog();
        catalog.Add<EditArguments, object>("edit", "Change", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { effects.Add(args.Value); return ValueTask.FromResult<object>(new { args.Value }); });
        return catalog;
    }
    public sealed record EditArguments(int Value);
}
