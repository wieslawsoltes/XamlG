using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentHarnessTests
{
    [Fact]
    public async Task Resume_preserves_completed_tools_and_does_not_repeat_the_user_prompt()
    {
        var writes = 0;
        var host = Host(() => ++writes);
        var provider = new ScriptedProvider(
            new("", [new("one", "edit", AutomationJson.Element(new Edit(0)))], new(10, 10), new object()),
            new("Done", [], new(12, 5), new object()));
        using var harness = new AgentHarness(host);
        var task = harness.CreateTask("Repair", provider, "test-model");
        var options = new AgentRunOptions { Limits = new() { RequestsPerRun = 1 }, Policy = new() { Profile = PermissionProfile.AutoEdit } };
        await harness.RunAsync(task.Id, "Make the change", options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(1, writes);
        await harness.RunAsync(task.Id, null, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, writes);
        var second = provider.Requests[1];
        Assert.Single(second.Messages, m => m.Kind == AgentMessageKind.User);
        Assert.Equal("one", Assert.Single(second.Messages, m => m.Kind == AgentMessageKind.ToolResult).ToolCallId);
        Assert.Equal(37, task.ReportedTokens);
    }

    [Fact]
    public async Task A_malformed_later_tool_prevents_the_entire_batch_from_executing()
    {
        var writes = 0;
        var provider = new ScriptedProvider(new AgentReply("", [
            new("one", "edit", AutomationJson.Element(new Edit(0))),
            new("two", "edit", AutomationJson.Element(new { wrong = 1 }))], new(1, 1), new object()));
        using var harness = new AgentHarness(Host(() => ++writes));
        var task = harness.CreateTask("Invalid", provider, "test-model");
        await Assert.ThrowsAsync<AutomationException>(() => harness.RunAsync(task.Id, "Change", new() { Policy = new() { Profile = PermissionProfile.FullAccess } }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, writes); Assert.Equal(AgentTaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task Oversized_batch_pauses_before_any_operation_and_resumes_without_regeneration()
    {
        var writes = 0;
        var provider = new ScriptedProvider(new("", [
            new("one", "edit", AutomationJson.Element(new Edit(0))),
            new("two", "edit", AutomationJson.Element(new Edit(1)))], new(1, 1), new object()), new("Done", [], new(1, 1), new object()));
        using var harness = new AgentHarness(Host(() => ++writes));
        var task = harness.CreateTask("Batch", provider, "test-model");
        var options = new AgentRunOptions { Limits = new() { ToolsPerRun = 1 }, Policy = new() { Profile = PermissionProfile.AutoEdit } };
        await harness.RunAsync(task.Id, "Change twice", options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(0, writes);
        await harness.RunAsync(task.Id, null, options with { Limits = options.Limits with { ToolsPerRun = 2 } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, writes); Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
    }

    [Fact]
    public async Task Stop_cancels_pending_review_without_executing_the_operation()
    {
        var writes = 0;
        var provider = new ScriptedProvider(new AgentReply("", [new("one", "edit", AutomationJson.Element(new Edit(0)))], new(1, 1), new object()));
        using var harness = new AgentHarness(Host(() => ++writes));
        var task = harness.CreateTask("Review", provider, "test-model");
        var reviewing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = harness.RunAsync(task.Id, "Change", new(), async (_, token) =>
        { reviewing.SetResult(); await Task.Delay(Timeout.Infinite, token); return AgentApproval.AllowOnce; }, cancellationToken: TestContext.Current.CancellationToken);
        await reviewing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); harness.Stop(); await run;
        Assert.Equal(AgentTaskStatus.Cancelled, task.Status); Assert.Equal(0, writes);
    }

    private static AutomationCatalog Host(Func<int> execute)
    {
        var host = new AutomationCatalog();
        host.Add<Edit, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => ValueTask.FromResult<object>(new { revision = execute() }));
        return host;
    }

    [Fact]
    public async Task Checkpoint_releases_native_history_but_preserves_all_user_requirements()
    {
        var provider = new ScriptedProvider(new("First", [], new(1, 1), "native-secret-one"),
            new("Second", [], new(1, 1), "native-secret-two"), new("Third", [], new(1, 1), "native-secret-three"));
        using var harness = new AgentHarness(Host(() => 0));
        var task = harness.CreateTask("Checkpoint", provider, "test-model");
        await harness.RunAsync(task.Id, "Keep keyboard navigation", new(), cancellationToken: TestContext.Current.CancellationToken);
        await harness.RunAsync(task.Id, "Preserve the dark theme", new(), cancellationToken: TestContext.Current.CancellationToken);
        harness.Compact(task.Id);
        await harness.RunAsync(task.Id, "Continue", new(), cancellationToken: TestContext.Current.CancellationToken);
        var request = provider.Requests[2];
        Assert.Equal(2, request.Messages.Count);
        Assert.Contains("Keep keyboard navigation", request.Messages[0].Text);
        Assert.Contains("Preserve the dark theme", request.Messages[0].Text);
        Assert.All(request.Messages, message => Assert.Null(message.Native));
        Assert.DoesNotContain("native-secret", harness.ExportTranscript(task.Id));
        Assert.Equal(1, task.CheckpointCount);
    }

    [Fact]
    public async Task Queued_follow_up_uses_the_same_run_and_observer_failures_do_not_interrupt_tools()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new WaitingProvider(entered, release);
        using var harness = new AgentHarness(Host(() => 0));
        harness.EventPublished += _ => throw new InvalidOperationException("A broken UI observer");
        var task = harness.CreateTask("Follow-up", provider, "test-model");
        var run = harness.RunAsync(task.Id, "First requirement", new(), cancellationToken: TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        harness.QueueMessage(task.Id, "Second requirement");
        release.SetResult(); await run;
        Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Empty(task.QueuedMessages);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(["First requirement", "Second requirement"], provider.Requests[1].Messages.Where(m => m.Kind == AgentMessageKind.User).Select(m => m.Text));
    }

    [Fact]
    public async Task Change_review_restores_selected_files_and_rejects_intervening_source_edits()
    {
        var workspace = new MemoryWorkspace();
        var provider = new ScriptedProvider(new("", [new("one", "edit", AutomationJson.Element(new Edit(0)))], new(1, 1), new object()), new("Done", [], new(1, 1), new object()));
        using var harness = new AgentHarness(Host(() => { workspace.Text = "agent edit"; return ++workspace.Revision; }), workspace);
        var task = harness.CreateTask("Changes", provider, "test-model");
        await harness.RunAsync(task.Id, "Change", new() { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken);
        var change = Assert.Single(task.Changes!.Files);
        Assert.Equal("before", change.Before); Assert.Equal("agent edit", change.After);
        workspace.Text = "user edit"; workspace.Revision++;
        await Assert.ThrowsAsync<AutomationException>(() => harness.RestoreChangesAsync(task.Id, ["View.axaml"], workspace.Revision, TestContext.Current.CancellationToken));
        Assert.Equal("user edit", workspace.Text);
        workspace.Text = "agent edit"; workspace.Revision++;
        var restored = await harness.RestoreChangesAsync(task.Id, ["View.axaml"], workspace.Revision, TestContext.Current.CancellationToken);
        Assert.Empty(restored.Files); Assert.Equal("before", workspace.Text);
    }

    private sealed class MemoryWorkspace : IAgentWorkspace
    {
        public string Text = "before";
        public int Revision;
        public Task<AgentWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new AgentWorkspaceSnapshot(Revision, new Dictionary<string, string> { ["View.axaml"] = Text }));
        public Task<AgentWorkspaceSnapshot> RestoreAsync(long expectedRevision, IReadOnlyList<AgentFileChange> files, CancellationToken cancellationToken)
        {
            if (expectedRevision != Revision || files[0].After != Text) throw new AutomationException("revision_conflict", "Source changed.");
            Text = files[0].Before!; Revision++; return CaptureAsync(cancellationToken);
        }
    }

    private sealed class WaitingProvider(TaskCompletionSource entered, TaskCompletionSource release) : IAgentProvider
    {
        public string Id => "test";
        public List<AgentRequest> Requests { get; } = [];
        public int GetContextBytes(AgentRequest request) => 100;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
        public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> delta, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(cancellationToken); }
            return new("Done", [], new(1, 1), new object());
        }
    }
    public sealed record Edit(long ExpectedRevision);
    private sealed class ScriptedProvider(params AgentReply[] replies) : IAgentProvider
    {
        public string Id => "test";
        public List<AgentRequest> Requests { get; } = [];
        public int GetContextBytes(AgentRequest request) => 100;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
        public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
        { Requests.Add(request); return Task.FromResult(replies[Requests.Count - 1]); }
    }
}
