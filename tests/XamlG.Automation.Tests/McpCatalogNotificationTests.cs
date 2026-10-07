using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XamlG.Automation;
using XamlG.Mcp;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class McpCatalogNotificationTests
{
    [Fact]
    public async Task Modern_http_subscriptions_are_filtered_routed_to_the_request_and_cancelled_independently()
    {
        var host = new MutableHost();
        var other = new AutomationCatalog();
        other.Add<Echo, Echo>("other_host", "Other host", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var sdkTools = new McpServerPrimitiveCollection<McpServerTool>();
        builder.Services.AddMcpServer(options => options.ToolCollection = sdkTools).WithHttpTransport().WithAutomation(host).WithAutomation(other);
        await using var server = builder.Build(); server.MapMcp("/mcp");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StartAsync(timeout.Token);
        try
        {
            var endpoint = new Uri(server.Urls.Single() + "/mcp");
            await using var first = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = endpoint }),
                new() { ProtocolVersion = "2026-07-28" }, cancellationToken: timeout.Token);
            await using var second = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = endpoint }),
                new() { ProtocolVersion = "2026-07-28" }, cancellationToken: timeout.Token);
            Assert.True(first.ServerCapabilities.Tools!.ListChanged);
            Assert.True(second.ServerCapabilities.Resources!.ListChanged);
            var firstMessages = Channel.CreateUnbounded<JsonRpcNotification>();
            var secondMessages = Channel.CreateUnbounded<JsonRpcNotification>();
            var observers = new List<IAsyncDisposable>();
            foreach (var method in new[] { NotificationMethods.SubscriptionsAcknowledgedNotification, NotificationMethods.ToolListChangedNotification,
                NotificationMethods.ResourceListChangedNotification, NotificationMethods.PromptListChangedNotification })
            {
                observers.Add(first.RegisterNotificationHandler(method, (item, _) => { firstMessages.Writer.TryWrite(item); return ValueTask.CompletedTask; }));
                observers.Add(second.RegisterNotificationHandler(method, (item, _) => { secondMessages.Writer.TryWrite(item); return ValueTask.CompletedTask; }));
            }
            using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            try
            {
                var firstListen = Listen(first, new("tools-stream"), new() { ToolsListChanged = true }, firstCancellation.Token);
                var firstAck = await firstMessages.Reader.ReadAsync(timeout.Token);
                Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, firstAck.Method);
                Assert.True(firstAck.Params!["notifications"]!["toolsListChanged"]!.GetValue<bool>());
                Assert.Null(firstAck.Params["notifications"]!["resourcesListChanged"]);
                var secondListen = Listen(second, new(42L), new() { ResourcesListChanged = true, PromptsListChanged = true,
                    ResourceSubscriptions = ["xamlg://not-supported"] }, secondCancellation.Token);
                var secondAck = await secondMessages.Reader.ReadAsync(timeout.Token);
                Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, secondAck.Method);
                Assert.Null(secondAck.Params!["notifications"]!["resourceSubscriptions"]);
                Assert.Null(secondAck.Params["notifications"]!["toolsListChanged"]);
                Assert.Equal(42L, secondAck.Params["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<long>());
                var catalog = new AutomationCatalog();
                catalog.Add<Echo, Echo>("echo", "echo", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
                catalog.AddResource(new("xamlg://catalog-http", "HTTP catalog", "test"), _ => ValueTask.FromResult("resource"));
                catalog.AddPrompt(new("inspect", "inspect", "prompt"));
                host.Replace(catalog);
                var toolChange = await firstMessages.Reader.ReadAsync(timeout.Token);
                Assert.Equal(NotificationMethods.ToolListChangedNotification, toolChange.Method);
                Assert.Equal("tools-stream", toolChange.Params!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>());
                var secondChanges = new[] { await secondMessages.Reader.ReadAsync(timeout.Token), await secondMessages.Reader.ReadAsync(timeout.Token) };
                Assert.Equal(new[] { NotificationMethods.PromptListChangedNotification, NotificationMethods.ResourceListChangedNotification }, secondChanges.Select(n => n.Method).Order().ToArray());
                Assert.All(secondChanges, n => Assert.Equal(42L, n.Params!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<long>()));
                Assert.Equal(2, (await first.ListToolsAsync(cancellationToken: timeout.Token)).Count);
                Assert.False(firstListen.IsCompleted); Assert.False(secondListen.IsCompleted);
                // SDK primitives registered by another component participate in discovery and notifications.
                sdkTools.Add(McpServerTool.Create(() => "independent", new() { Name = "independent" }));
                Assert.Equal(NotificationMethods.ToolListChangedNotification, (await firstMessages.Reader.ReadAsync(timeout.Token)).Method);
                firstCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstListen);
                host.Replace(new AutomationCatalog());
                for (var i = 0; i < 2; i++) Assert.Equal(42L, (await secondMessages.Reader.ReadAsync(timeout.Token)).Params!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<long>());
                Assert.False(secondListen.IsCompleted);
                Assert.Equal(2, (await second.ListToolsAsync(cancellationToken: timeout.Token)).Count);
                // A request granting no supported notifications acknowledges once and releases its stream.
                await Listen(first, new("empty"), new() { ResourceSubscriptions = ["xamlg://not-supported"] }, timeout.Token);
                var emptyAck = await firstMessages.Reader.ReadAsync(timeout.Token);
                Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, emptyAck.Method);
                Assert.Empty(emptyAck.Params!["notifications"]!.AsObject());
                Assert.False(firstMessages.Reader.TryRead(out _)); Assert.False(secondMessages.Reader.TryRead(out _));
                secondCancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondListen);
            }
            finally
            {
                firstCancellation.Cancel(); secondCancellation.Cancel();
                foreach (var observer in observers) await observer.DisposeAsync();
            }
        }
        finally { await server.StopAsync(timeout.Token); }
        await server.DisposeAsync();
        Assert.Equal(0, host.ObserverCount);
    }

    private static Task<JsonRpcResponse> Listen(McpClient client, RequestId id, SubscriptionsListenNotifications notifications, CancellationToken cancellationToken) =>
        client.SendRequestAsync(new JsonRpcRequest { Id = id, Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(new SubscriptionsListenRequestParams { Notifications = notifications }) }, cancellationToken);

    [Fact]
    public void Completed_request_options_and_collections_are_not_retained_by_catalog_observers()
    {
        var host = new MutableHost(); var services = new ServiceCollection();
        services.AddLogging(); services.AddMcpServer().WithAutomation(host);
        using var provider = services.BuildServiceProvider();
        var references = CreateRequestOptions(provider.GetRequiredService<IOptionsFactory<McpServerOptions>>());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        // Refresh remains safe after the expired request collections have been collected.
        host.Replace(new AutomationCatalog());
        Assert.Equal(1, host.ObserverCount);
        provider.Dispose(); Assert.Equal(0, host.ObserverCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateRequestOptions(IOptionsFactory<McpServerOptions> factory)
    {
        var options = factory.Create(Options.DefaultName);
        return [new(options), new(options.ToolCollection!), new(options.ResourceCollection!), new(options.PromptCollection!)];
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Catalog_changes_use_sdk_notifications_and_preserve_other_registered_tools(string protocol)
    {
        var host = new MutableHost(); var inbound = new Pipe(); var outbound = new Pipe();
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStreamServerTransport(inbound.Reader.AsStream(), outbound.Writer.AsStream())
            .WithTools([McpServerTool.Create(() => "standalone", new() { Name = "standalone" })]).WithAutomation(host);
        using var server = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(inbound.Writer.AsStream(), outbound.Reader.AsStream()),
                new() { ProtocolVersion = protocol }, cancellationToken: timeout.Token);
            Assert.True(client.ServerCapabilities.Tools!.ListChanged);
            Assert.True(client.ServerCapabilities.Resources!.ListChanged);
            Assert.True(client.ServerCapabilities.Prompts!.ListChanged);
            Assert.Equal("standalone", Assert.Single(await client.ListToolsAsync(cancellationToken: timeout.Token)).Name);
            var notifications = Channel.CreateUnbounded<JsonRpcNotification>();
            await using var tools = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, Receive);
            await using var resources = client.RegisterNotificationHandler(NotificationMethods.ResourceListChangedNotification, Receive);
            await using var prompts = client.RegisterNotificationHandler(NotificationMethods.PromptListChangedNotification, Receive);
            using var listening = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task<JsonRpcResponse>? listen = null;
            if (protocol == "2026-07-28")
            {
                var acknowledged = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var ack = client.RegisterNotificationHandler(NotificationMethods.SubscriptionsAcknowledgedNotification,
                    (notification, _) => { acknowledged.TrySetResult(notification); return ValueTask.CompletedTask; });
                listen = client.SendRequestAsync(new JsonRpcRequest
                {
                    Id = new RequestId("catalog-listener"), Method = RequestMethods.SubscriptionsListen,
                    Params = JsonSerializer.SerializeToNode(new SubscriptionsListenRequestParams
                    { Notifications = new() { ToolsListChanged = true, ResourcesListChanged = true, PromptsListChanged = true } })
                }, listening.Token);
                var result = await acknowledged.Task.WaitAsync(timeout.Token);
                Assert.True(result.Params!["notifications"]!["toolsListChanged"]!.GetValue<bool>());
                Assert.Equal("catalog-listener", result.Params["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>());
            }
            var catalog = new AutomationCatalog();
            catalog.Add<Echo, Echo>("echo", "echo", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
            catalog.AddResource(new("xamlg://catalog-test", "Catalog test", "test"), _ => ValueTask.FromResult("current resource"));
            catalog.AddPrompt(new("inspect", "inspect", "Current prompt"));
            host.Replace(catalog);
            var received = new List<JsonRpcNotification>();
            for (var i = 0; i < 3; i++) received.Add(await notifications.Reader.ReadAsync(timeout.Token));
            Assert.Equal(3, received.Select(n => n.Method).Distinct().Count());
            if (protocol == "2026-07-28")
                Assert.All(received, n => Assert.Equal("catalog-listener", n.Params!["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>()));
            Assert.Equal(2, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
            var echo = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "current catalog" }, cancellationToken: timeout.Token);
            Assert.Equal("current catalog", echo.StructuredContent!.Value.GetProperty("text").GetString());
            Assert.Single(await client.ListResourcesAsync(cancellationToken: timeout.Token));
            Assert.Single(await client.ListPromptsAsync(cancellationToken: timeout.Token));
            host.Replace(new AutomationCatalog());
            for (var i = 0; i < 3; i++) await notifications.Reader.ReadAsync(timeout.Token);
            Assert.Equal("standalone", Assert.Single(await client.ListToolsAsync(cancellationToken: timeout.Token)).Name);
            Assert.Empty(await client.ListResourcesAsync(cancellationToken: timeout.Token));
            if (listen != null)
            {
                listening.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
            }
            ValueTask Receive(JsonRpcNotification notification, CancellationToken _)
            { notifications.Writer.TryWrite(notification); return ValueTask.CompletedTask; }
        }
        finally { await server.StopAsync(timeout.Token); }
        server.Dispose();
        Assert.Equal(0, host.ObserverCount);
    }

    public sealed record Echo(string Text);
    private sealed class MutableHost : IAutomationHost, IAutomationCatalogEvents
    {
        private AutomationCatalog _catalog = new();
        private Action? _changed;
        public int ObserverCount => _changed?.GetInvocationList().Length ?? 0;
        public event Action? CatalogChanged { add => _changed += value; remove => _changed -= value; }
        public void Replace(AutomationCatalog catalog) { _catalog = catalog; _changed?.Invoke(); }
        public IReadOnlyList<AutomationTool> Tools => _catalog.Tools;
        public IReadOnlyList<AutomationResource> Resources => _catalog.Resources;
        public IReadOnlyList<AutomationPrompt> Prompts => _catalog.Prompts;
        public ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context) => _catalog.CallAsync(name, arguments, context);
        public ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context) => _catalog.ReadResourceAsync(uri, context);
    }
}
