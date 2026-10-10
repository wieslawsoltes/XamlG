using System.Text.Json;
using XamlG.Agents;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentPersistenceTests
{
    [Fact]
    public async Task Interrupted_effect_is_not_replayed_and_native_history_queue_draft_and_usage_survive()
    {
        var writes = 0; var catalog = new AutomationCatalog();
        catalog.Add<NoArgs, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit, (_, _) => ValueTask.FromResult<object>(new { writes = ++writes }));
        var provider = new StoredProvider();
        provider.Steps.Enqueue(request => new("", [new("call-1", "edit", AutomationJson.Element(new { }))], new(21, 8), AutomationJson.Element(new { signature = "opaque-private" })));
        provider.Steps.Enqueue(_ => new("Done", [], new(4, 5), AutomationJson.Element(new { signature = "final-private" })));
        using var harness = new AgentHarness(catalog); var task = harness.CreateTask("Durable", provider, "fixture", TestContext.Current.CancellationToken, workspaceIdentity: "project-1");
        task.Draft = "next draft"; harness.QueueMessage(task.Id, "queued instruction");
        AgentSessionSnapshot? checkpoint = null;
        harness.PersistSession = (state, _) => { if (state.Tasks[0].ExecutingToolId != null) checkpoint = state; return Task.CompletedTask; };
        await harness.RunAsync(task.Id, "Edit once", new() { ContinueQueuedMessages = false, Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, writes); Assert.NotNull(checkpoint);
        var stored = JsonSerializer.Serialize(checkpoint, AutomationJson.Options);
        Assert.Contains("opaque-private", stored); Assert.DoesNotContain("opaque-private", harness.ExportTranscript(task.Id));
        using var restored = new AgentHarness(catalog);
        restored.RestoreSession(JsonSerializer.Deserialize<AgentSessionSnapshot>(stored, AutomationJson.Options)!, (_, _) => provider, TestContext.Current.CancellationToken, workspaceIdentity: "project-1");
        var saved = restored.GetTask(task.Id);
        Assert.Equal(AgentTaskStatus.Paused, saved.Status); Assert.Equal("next draft", saved.Draft);
        Assert.Equal("queued instruction", Assert.Single(saved.Queue.Messages).Text); Assert.Equal(29, saved.TotalTokens);
        Assert.Null(restored.ActivePermissions);
        provider.Steps.Enqueue(request =>
        {
            Assert.Contains(request.Messages, message => message.Kind == AgentMessageKind.ToolResult && message.ToolCallId == "call-1" && message.Text.Contains("effects may have occurred", StringComparison.Ordinal));
            Assert.Contains(request.Messages, message => message.Native is JsonElement native && native.GetProperty("signature").GetString() == "opaque-private");
            return new("Inspected the completed edit", [], new(1, 1), AutomationJson.Element(new { signature = "continued" }));
        });
        await restored.RunAsync(task.Id, null, new() { ContinueQueuedMessages = false, Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, writes); Assert.Equal(AgentTaskStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task A_failed_durable_write_prevents_the_tool_effect()
    {
        var writes = 0; var catalog = new AutomationCatalog();
        catalog.Add<NoArgs, object>("edit", "Edit", AutomationScope.Source, AutomationEffect.Edit, (_, _) => ValueTask.FromResult<object>(++writes));
        var provider = new StoredProvider();
        provider.Steps.Enqueue(_ => new("", [new("call-1", "edit", AutomationJson.Element(new { }))], new(1, 1), AutomationJson.Element(new { signature = "private" })));
        using var harness = new AgentHarness(catalog); var task = harness.CreateTask("Storage failure", provider, "fixture", TestContext.Current.CancellationToken);
        harness.PersistSession = (snapshot, _) => snapshot.Tasks[0].ExecutingToolId != null ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => harness.RunAsync(task.Id, "Edit", new() { ContinueQueuedMessages = false, Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Reconnection_binds_only_the_same_project_and_never_restores_permission_grants()
    {
        using var previous = new CancellationTokenSource(); using var next = new CancellationTokenSource();
        var provider = new StoredProvider(); using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("Saved", provider, "fixture", previous.Token, "project-a"); previous.Cancel();
        Assert.True(task.IsPreviousWorkspace);
        Assert.False(harness.ReconnectWorkspace("project-b", next.Token)); Assert.True(task.IsPreviousWorkspace);
        Assert.True(harness.ReconnectWorkspace("project-a", next.Token)); Assert.False(task.IsPreviousWorkspace);
        Assert.Null(harness.ActivePermissions);
    }

    [Fact]
    public async Task Focused_catalog_can_discover_and_enable_every_tool()
    {
        var catalog = new AutomationCatalog(); var calls = 0;
        for (var i = 0; i < 160; i++) catalog.Add<NoArgs, object>("tool_" + i, "An available IDE capability", AutomationScope.Runtime, AutomationEffect.Read, (_, _) => ValueTask.FromResult<object>(++calls));
        var provider = new StoredProvider();
        provider.Steps.Enqueue(request =>
        {
            Assert.True(request.Tools.Count < 10); Assert.DoesNotContain(request.Tools, tool => tool.Name == "tool_159");
            return new("", [new("find", "xamlg_agent_tools", AutomationJson.Element(new { enable = new[] { "tool_159" } }))], new(1, 1), AutomationJson.Element(new { step = 1 }));
        });
        provider.Steps.Enqueue(request =>
        {
            Assert.Contains(request.Tools, tool => tool.Name == "tool_159");
            return new("", [new("read", "tool_159", AutomationJson.Element(new { }))], new(1, 1), AutomationJson.Element(new { step = 2 }));
        });
        provider.Steps.Enqueue(_ => new("Done", [], new(1, 1), AutomationJson.Element(new { step = 3 })));
        using var harness = new AgentHarness(catalog); var task = harness.CreateTask("Discovery", provider, "fixture", TestContext.Current.CancellationToken);
        await harness.RunAsync(task.Id, "Use the last tool", new(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(AgentTaskStatus.Completed, task.Status);
        Assert.Contains("tool_159", harness.CaptureSession().Tasks[0].EnabledTools);
        Assert.Equal(164, harness.RequestTools(task, fullCatalog: true).Count);
    }

    [Fact]
    public async Task State_cursor_is_small_and_rejects_a_previous_session_epoch()
    {
        using var session = new AgentWorkbenchSession(new AutomationCatalog(), [new StoredProvider()]);
        var empty = AutomationJson.Element(new { });
        var state = await session.ExecuteAsync("state", empty, TestContext.Current.CancellationToken);
        var cursor = AutomationJson.Element(new { sessionId = state.GetProperty("sessionId").GetString(), revision = state.GetProperty("revision").GetInt64() });
        var same = await session.ExecuteAsync("state", cursor, TestContext.Current.CancellationToken);
        Assert.True(same.GetProperty("unchanged").GetBoolean()); Assert.True(same.GetRawText().Length < 120);
        var foreign = await session.ExecuteAsync("state", AutomationJson.Element(new { sessionId = "previous-instance", revision = state.GetProperty("revision").GetInt64() }), TestContext.Current.CancellationToken);
        Assert.True(foreign.TryGetProperty("tasks", out _));
        await session.ExecuteAsync("create", AutomationJson.Element(new { name = "new", provider = "stored", model = "fixture" }), TestContext.Current.CancellationToken);
        var changed = await session.ExecuteAsync("state", cursor, TestContext.Current.CancellationToken);
        Assert.Single(changed.GetProperty("tasks").EnumerateArray());
    }

    public sealed record NoArgs;
    private sealed class StoredProvider : IAgentProvider, IAgentProviderState
    {
        public string Id => "stored";
        public Queue<Func<AgentRequest, AgentReply>> Steps { get; } = new();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(["fixture"]);
        public int GetContextBytes(AgentRequest request) => 1024;
        public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken) => Task.FromResult(Steps.Dequeue()(request));
        public JsonElement SaveNative(object native) => ((JsonElement)native).Clone();
        public object RestoreNative(JsonElement native) => native.Clone();
    }
}
