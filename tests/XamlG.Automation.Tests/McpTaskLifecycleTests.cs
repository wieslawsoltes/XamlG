using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using XamlG.Mcp;
using Xunit;

#pragma warning disable MCPEXP001 // Exercise the official SDK Tasks extension.

namespace XamlG.Automation.Tests;

public sealed class McpTaskLifecycleTests
{
    [Fact]
    public async Task Task_results_and_subscription_streams_belong_to_the_authenticated_principal()
    {
        await using var fixture = await TaskFixture.StartAsync();
        var token = fixture.Token;
        await using var owner = await fixture.ClientAsync("owner");
        await using var other = await fixture.ClientAsync("other");
        var created = await owner.CallToolAsTaskAsync(Wait("owned"), token);
        Assert.True(created.IsTask);
        var id = created.TaskCreated!.TaskId;
        Assert.Equal("owner", (await fixture.Wait("owned").Entered.Task.WaitAsync(token)).PrincipalId);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await other.GetTaskAsync(id, token));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await other.CancelTaskAsync(id, token));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await other.UpdateTaskAsync(new() { TaskId = id }, token));
        Assert.False((await fixture.Wait("owned").Entered.Task).CancellationToken.IsCancellationRequested);
        Assert.IsType<WorkingTaskResult>(await owner.GetTaskAsync(id, token));
        await owner.UpdateTaskAsync(new() { TaskId = id }, token);

        var ownerAcknowledgements = Channel.CreateUnbounded<JsonRpcNotification>();
        var otherAcknowledgements = Channel.CreateUnbounded<JsonRpcNotification>();
        var ownerMessages = Channel.CreateUnbounded<JsonRpcNotification>();
        var otherMessages = Channel.CreateUnbounded<JsonRpcNotification>();
        // The SDK dispatches different notification handlers concurrently. Their
        // callbacks cannot certify wire ordering; raw SSE browser cases cover that.
        await using var ownerAck = Observe(owner, NotificationMethods.SubscriptionsAcknowledgedNotification, ownerAcknowledgements);
        await using var ownerStatus = Observe(owner, TasksProtocol.NotificationTaskStatus, ownerMessages);
        await using var otherAck = Observe(other, NotificationMethods.SubscriptionsAcknowledgedNotification, otherAcknowledgements);
        await using var otherStatus = Observe(other, TasksProtocol.NotificationTaskStatus, otherMessages);
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stream = Listen(owner, "owner-stream", [id, "unknown-task"], listening.Token);
        try
        {
            var acknowledgement = await ownerAcknowledgements.Reader.ReadAsync(token);
            Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, acknowledgement.Method);
            Assert.Equal(new[] { id }, acknowledgement.Params!["notifications"]!["taskIds"]!.AsArray().Select(value => value!.GetValue<string>()));
            await Listen(other, "other-stream", [id], token);
            Assert.Null((await otherAcknowledgements.Reader.ReadAsync(token)).Params!["notifications"]!["taskIds"]);
            fixture.Wait("owned").Result.SetResult("observed source revision");
            JsonRpcNotification completed;
            do { completed = await ownerMessages.Reader.ReadAsync(token); }
            while (completed.Params?["status"]?.GetValue<string>() != "completed");
            Assert.Equal("owner-stream", completed.Params!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>());
            var result = Assert.IsType<CompletedTaskResult>(await owner.GetTaskAsync(id, token));
            Assert.Equal("observed source revision", result.Result.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.False(otherMessages.Reader.TryRead(out _));
            Assert.Null(Assert.Single(fixture.Store.LocalInventory).Result);
        }
        finally { listening.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream); }
    }

    [Fact]
    public async Task Cancellation_reaches_the_wait_and_replacement_retires_old_workspace_tasks()
    {
        await using var fixture = await TaskFixture.StartAsync();
        await using var client = await fixture.ClientAsync("owner");
        var token = fixture.Token;
        var cancelled = (await client.CallToolAsTaskAsync(Wait("cancel"), token)).TaskCreated!.TaskId;
        await fixture.Wait("cancel").Entered.Task.WaitAsync(token);
        await client.CancelTaskAsync(cancelled, token);
        await fixture.Wait("cancel").Exited.Task.WaitAsync(token);
        Assert.True(fixture.Wait("cancel").Cancelled);
        Assert.IsType<CancelledTaskResult>(await client.GetTaskAsync(cancelled, token));
        fixture.Wait("cancel").Result.TrySetResult("late result must be ignored");
        Assert.IsType<CancelledTaskResult>(await client.GetTaskAsync(cancelled, token));

        var replaced = (await client.CallToolAsTaskAsync(Wait("replace"), token)).TaskCreated!.TaskId;
        await fixture.Wait("replace").Entered.Task.WaitAsync(token);
        fixture.ReplaceWorkspace();
        await fixture.Wait("replace").Exited.Task.WaitAsync(token);
        Assert.True(fixture.Wait("replace").Cancelled);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.GetTaskAsync(replaced, token));
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.GetTaskAsync(cancelled, token));
        Assert.Empty(fixture.Store.LocalInventory);
        var current = (await client.CallToolAsTaskAsync(Wait("current"), token)).TaskCreated!.TaskId;
        fixture.Wait("current").Result.SetResult("current workspace");
        Assert.IsType<CompletedTaskResult>(await TerminalAsync(client, current, token));
    }

    [Fact]
    public async Task Retention_limit_requires_finished_task_cleanup_and_oversized_results_are_not_retained()
    {
        await using var fixture = await TaskFixture.StartAsync();
        await using var client = await fixture.ClientAsync("owner");
        var token = fixture.Token;
        var ids = new List<string>();
        for (var i = 0; i < 32; i++) ids.Add((await client.CallToolAsTaskAsync(Wait("wait-" + i), token)).TaskCreated!.TaskId);
        Assert.Equal(32, fixture.Store.LocalInventory.Count);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.CallToolAsTaskAsync(Wait("overflow"), token));
        Assert.False(fixture.Wait("overflow").Entered.Task.IsCompleted);
        foreach (var id in ids) fixture.Store.CancelLocal(id);
        fixture.Store.ClearFinishedLocal();
        Assert.Empty(fixture.Store.LocalInventory);
        var large = (await client.CallToolAsTaskAsync(Wait("large"), token)).TaskCreated!.TaskId;
        fixture.Wait("large").Result.SetResult(new string('x', 70000));
        var rejected = Assert.IsType<CompletedTaskResult>(await TerminalAsync(client, large, token));
        Assert.True(rejected.Result.GetProperty("isError").GetBoolean());
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(rejected.Result.GetRawText()) < 65536);
        Assert.DoesNotContain(new string('x', 1000), rejected.Result.GetRawText());
        Assert.Null(Assert.Single(fixture.Store.LocalInventory).Result);
    }

    [Theory]
    [InlineData("2025-11-25", false)]
    [InlineData("2026-07-28", true)]
    public async Task Only_modern_opted_in_waits_create_tasks_and_permission_denial_prevents_execution(string protocol, bool asynchronous)
    {
        await using var fixture = await TaskFixture.StartAsync();
        await using var client = await fixture.ClientAsync("owner", protocol);
        var token = fixture.Token;
        var ordinary = await client.CallToolAsTaskAsync(new() { Name = "ordinary", Arguments = Arguments("ordinary") }, token);
        Assert.False(ordinary.IsTask); Assert.Empty(fixture.Store.LocalInventory);
        fixture.Wait("allowed").Result.SetResult("allowed result");
        var allowed = await client.CallToolAsTaskAsync(Wait("allowed"), token);
        Assert.Equal(asynchronous, allowed.IsTask);
        if (asynchronous) Assert.IsType<CompletedTaskResult>(await TerminalAsync(client, allowed.TaskCreated!.TaskId, token));
        fixture.Allow = false;
        var denied = await client.CallToolAsTaskAsync(Wait("denied"), token);
        var result = asynchronous
            ? Assert.IsType<CompletedTaskResult>(await TerminalAsync(client, denied.TaskCreated!.TaskId, token)).Result
            : JsonSerializer.SerializeToElement(denied.Result);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(fixture.Wait("denied").Entered.Task.IsCompleted);
    }

    private static CallToolRequestParams Wait(string key) => new() { Name = "xamlg_wait", Arguments = Arguments(key) };
    private static Dictionary<string, JsonElement> Arguments(string key) => new() { ["key"] = AutomationJson.Element(key) };
    private static IAsyncDisposable Observe(McpClient client, string method, Channel<JsonRpcNotification> channel) =>
        client.RegisterNotificationHandler(method, (item, _) => { channel.Writer.TryWrite(item); return ValueTask.CompletedTask; });
    private static Task<JsonRpcResponse> Listen(McpClient client, string stream, string[] ids, CancellationToken cancellationToken) =>
        client.SendRequestAsync(new JsonRpcRequest { Id = new(stream), Method = RequestMethods.SubscriptionsListen,
            Params = new JsonObject { ["notifications"] = new JsonObject { ["taskIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) } } }, cancellationToken);
    private static async Task<GetTaskResult> TerminalAsync(McpClient client, string id, CancellationToken cancellationToken)
    {
        for (;;)
        {
            var task = await client.GetTaskAsync(id, cancellationToken);
            if (task is not WorkingTaskResult) return task;
            await Task.Delay(10, cancellationToken);
        }
    }

    public sealed record WaitArgs(string Key);
    public sealed record WaitResult(string Value, string? Principal);
    private sealed class WaitControl
    {
        public TaskCompletionSource<AutomationCallContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
    }
    private sealed class TaskFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private readonly List<CancellationTokenSource> _workspaces = [new()];
        private readonly ConcurrentDictionary<string, WaitControl> _waits = new();
        private WebApplication _server = null!;
        public AutomationMcpTaskStore Store { get; } = new();
        public bool Allow = true;
        public CancellationToken Token => _timeout.Token;
        public WaitControl Wait(string key) => _waits.GetOrAdd(key, _ => new());
        public static async Task<TaskFixture> StartAsync()
        {
            var fixture = new TaskFixture();
            var catalog = new AutomationCatalog((_, _) => ValueTask.FromResult(fixture.Allow));
            catalog.Add<WaitArgs, WaitResult>("xamlg_wait", "Wait for an observed change", AutomationScope.Project, AutomationEffect.Read, async (args, context) =>
            {
                var wait = fixture.Wait(args.Key); wait.Entered.TrySetResult(context);
                try { return new(await wait.Result.Task.WaitAsync(context.CancellationToken), context.PrincipalId); }
                finally { wait.Cancelled = context.CancellationToken.IsCancellationRequested; wait.Exited.TrySetResult(); }
            });
            catalog.Add<WaitArgs, WaitResult>("ordinary", "An ordinary synchronous tool", AutomationScope.Project, AutomationEffect.Read,
                (args, context) => ValueTask.FromResult(new WaitResult(args.Key, context.PrincipalId)));
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport().WithAutomation(catalog).WithAutomationTasks(fixture.Store, () => fixture._workspaces[^1].Token);
            fixture._server = builder.Build();
            fixture._server.Use(async (context, next) =>
            {
                context.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, context.Request.Headers["X-Test-Principal"].ToString())], "fixture"));
                await next(context);
            });
            fixture._server.MapMcp("/mcp"); await fixture._server.StartAsync(fixture.Token); return fixture;
        }
        public async Task<McpClient> ClientAsync(string principal, string protocol = "2026-07-28") =>
            await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(_server.Urls.Single() + "/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["X-Test-Principal"] = principal } }),
                new() { ProtocolVersion = protocol, ClientInfo = new() { Name = "same-untrusted-client-name", Version = "1" } }, cancellationToken: Token);
        public void ReplaceWorkspace() { var previous = _workspaces[^1]; _workspaces.Add(new()); previous.Cancel(); }
        public async ValueTask DisposeAsync()
        {
            foreach (var workspace in _workspaces) workspace.Cancel();
            await _server.StopAsync(CancellationToken.None); await _server.DisposeAsync();
            Store.Dispose(); foreach (var workspace in _workspaces) workspace.Dispose(); _timeout.Dispose();
        }
    }
}
