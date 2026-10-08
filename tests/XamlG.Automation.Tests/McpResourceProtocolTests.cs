using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XamlG.Mcp;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class McpResourceProtocolTests
{
    private const string Template = "xamlg://source/{path}";

    [Fact]
    public async Task Template_reads_and_completion_preserve_encoded_paths_principal_and_permission_checks()
    {
        var allow = true;
        var catalog = new AutomationCatalog((_, _) => ValueTask.FromResult(allow));
        catalog.Add<PathArgs, string>("read_source", "Read source", AutomationScope.Source, AutomationEffect.Read,
            (args, context) => ValueTask.FromResult(context.PrincipalId + "|" + args.Path));
        catalog.AddResourceTemplate(new(Template, "Source", "Source text"), async (args, context) =>
            (await catalog.CallAsync("read_source", AutomationJson.Element(new PathArgs(args["path"])), context)).GetString()!,
            async (argument, value, context) =>
            {
                Assert.Equal("path", argument);
                var prefix = (await catalog.CallAsync("read_source", AutomationJson.Element(new PathArgs(value)), context)).GetString();
                return new(Enumerable.Range(0, 105).Select(index => prefix + index).ToArray(), 105, false);
            });
        await using var fixture = await ResourceFixture.StartAsync(catalog);
        await using var owner = await fixture.ClientAsync("owner");
        await using var other = await fixture.ClientAsync("other");
        var token = fixture.Token;
        Assert.Empty(await owner.ListResourcesAsync(cancellationToken: token));
        Assert.Equal(Template, Assert.Single(await owner.ListResourceTemplatesAsync(cancellationToken: token)).UriTemplate);
        const string path = "Views/雪 & #%?/Main.axaml";
        var uri = "xamlg://source/" + Uri.EscapeDataString(path);
        var read = await owner.ReadResourceAsync(uri, cancellationToken: token);
        Assert.Equal("owner|" + path, Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
        var completion = await Complete(other, "Views/", token);
        var values = completion["completion"]!["values"]!.AsArray();
        Assert.Equal(100, values.Count);
        Assert.All(values, value => Assert.StartsWith("other|Views/", value!.GetValue<string>()));
        Assert.Equal(105, completion["completion"]!["total"]!.GetValue<int>());
        Assert.True(completion["completion"]!["hasMore"]!.GetValue<bool>());
        allow = false;
        await Assert.ThrowsAsync<McpProtocolException>(async () => await owner.ReadResourceAsync(uri, cancellationToken: token));
        await Assert.ThrowsAsync<McpProtocolException>(() => Complete(other, "Views/", token));
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Resource_notifications_are_uri_only_exact_filtered_and_stop_after_unsubscription(string protocol)
    {
        var catalog = new AutomationCatalog();
        catalog.AddResourceTemplate(new(Template, "Source", "Source"), (_, _) => ValueTask.FromResult("private source payload"));
        await using var fixture = await ResourceFixture.StartAsync(catalog);
        await using var first = await fixture.ClientAsync("first", protocol);
        await using var second = await fixture.ClientAsync("second", protocol);
        var token = fixture.Token;
        var firstMessages = Channel.CreateUnbounded<JsonRpcNotification>();
        var secondMessages = Channel.CreateUnbounded<JsonRpcNotification>();
        await using var firstAck = Observe(first, NotificationMethods.SubscriptionsAcknowledgedNotification, firstMessages);
        await using var secondAck = Observe(second, NotificationMethods.SubscriptionsAcknowledgedNotification, secondMessages);
        await using var firstUpdates = Observe(first, NotificationMethods.ResourceUpdatedNotification, firstMessages);
        await using var secondUpdates = Observe(second, NotificationMethods.ResourceUpdatedNotification, secondMessages);
        using var firstListening = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var secondListening = CancellationTokenSource.CreateLinkedTokenSource(token);
        const string firstUri = "xamlg://source/First.axaml", secondUri = "xamlg://source/Second.axaml";
        Task<JsonRpcResponse>? firstStream = null, secondStream = null;
        try
        {
            if (protocol == "2026-07-28")
            {
                firstStream = Listen(first, "first-stream", [firstUri, firstUri, "xamlg://unknown"], firstListening.Token);
                var ack = await firstMessages.Reader.ReadAsync(token);
                Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, ack.Method);
                Assert.Equal(firstUri, Assert.Single(ack.Params!["notifications"]!["resourceSubscriptions"]!.AsArray())!.GetValue<string>());
                secondStream = Listen(second, "second-stream", [secondUri], secondListening.Token);
                Assert.Equal(NotificationMethods.SubscriptionsAcknowledgedNotification, (await secondMessages.Reader.ReadAsync(token)).Method);
                await Assert.ThrowsAsync<McpProtocolException>(() => Listen(first, "too-many", Enumerable.Range(0, 129).Select(i => "xamlg://source/" + i).ToArray(), token));
            }
            else
            {
                await Raw(first, "resources/subscribe", new() { ["uri"] = firstUri }, token);
                await Raw(second, "resources/subscribe", new() { ["uri"] = secondUri }, token);
                await Assert.ThrowsAsync<McpProtocolException>(() => Raw(first, "resources/subscribe", new() { ["uri"] = "xamlg://unknown" }, token));
            }
            catalog.NotifyResourceChanged(firstUri + "-different");
            catalog.NotifyResourceChanged(firstUri);
            catalog.NotifyResourceChanged(secondUri);
            AssertUpdate(await firstMessages.Reader.ReadAsync(token), firstUri, protocol == "2026-07-28" ? "first-stream" : null);
            AssertUpdate(await secondMessages.Reader.ReadAsync(token), secondUri, protocol == "2026-07-28" ? "second-stream" : null);
            if (firstStream != null)
            {
                firstListening.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStream);
            }
            else await Raw(first, "resources/unsubscribe", new() { ["uri"] = firstUri }, token);
            catalog.NotifyResourceChanged(firstUri);
            catalog.NotifyResourceChanged(secondUri);
            AssertUpdate(await secondMessages.Reader.ReadAsync(token), secondUri, protocol == "2026-07-28" ? "second-stream" : null);
            using (var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstMessages.Reader.ReadAsync(quiet.Token));
            if (firstStream == null)
            {
                // Closing the last legacy subscription must permit a fresh one.
                await Raw(first, "resources/subscribe", new() { ["uri"] = firstUri }, token);
                catalog.NotifyResourceChanged(firstUri);
                AssertUpdate(await firstMessages.Reader.ReadAsync(token), firstUri, null);
            }
        }
        finally
        {
            firstListening.Cancel(); secondListening.Cancel();
            if (secondStream != null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondStream);
        }
    }

    [Fact]
    public async Task Catalog_pages_are_complete_ordered_and_reject_stale_or_cross_catalog_cursors()
    {
        var catalog = new AutomationCatalog();
        for (var index = 204; index >= 0; index--)
        {
            var key = index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            catalog.Add<PathArgs, PathArgs>("tool_" + key, "Read", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
            catalog.AddResource(new("xamlg://resource/" + key, key, "Resource"), _ => ValueTask.FromResult("resource"));
            catalog.AddResourceTemplate(new("xamlg://template/" + key + "/{path}", key, "Template"), (_, _) => ValueTask.FromResult("template"));
            catalog.AddPrompt(new("prompt_" + key, "Prompt", "Read source"));
        }
        await using var fixture = await ResourceFixture.StartAsync(catalog);
        await using var client = await fixture.ClientAsync("owner");
        var token = fixture.Token;
        foreach (var (method, field, key) in new[] { ("tools/list", "tools", "name"), ("resources/list", "resources", "uri"),
            ("resources/templates/list", "resourceTemplates", "uriTemplate"), ("prompts/list", "prompts", "name") })
        {
            string? cursor = null;
            var items = new List<string>();
            foreach (var size in new[] { 100, 100, 5 })
            {
                var page = await Raw(client, method, new() { ["cursor"] = cursor }, token);
                var values = page[field]!.AsArray(); Assert.Equal(size, values.Count);
                items.AddRange(values.Select(value => value![key]!.GetValue<string>()));
                cursor = page["nextCursor"]?.GetValue<string>();
                if (size == 100) Assert.NotNull(cursor);
            }
            Assert.Null(cursor); Assert.Equal(205, items.Distinct().Count());
            Assert.Equal(items.Order(StringComparer.Ordinal), items);
        }
        var first = await Raw(client, "tools/list", new(), token);
        var toolsCursor = first["nextCursor"]!.GetValue<string>();
        await Assert.ThrowsAsync<McpProtocolException>(() => Raw(client, "resources/list", new() { ["cursor"] = toolsCursor }, token));
        await Assert.ThrowsAsync<McpProtocolException>(() => Raw(client, "tools/list", new() { ["cursor"] = "invalid cursor" }, token));
        catalog.Add<PathArgs, PathArgs>("new_tool", "New", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
        await Assert.ThrowsAsync<McpProtocolException>(() => Raw(client, "tools/list", new() { ["cursor"] = toolsCursor }, token));
        Assert.Equal(206, (await client.ListToolsAsync(cancellationToken: token)).Count);
    }

    [Fact]
    public async Task Explicit_embedding_list_handler_retains_its_own_cursor_and_page_size()
    {
        var catalog = new AutomationCatalog();
        await using var fixture = await ResourceFixture.StartAsync(catalog, options => options.Handlers.ListResourcesHandler = (request, _) =>
        {
            Assert.Equal("embedding-cursor", request.Params?.Cursor);
            return ValueTask.FromResult(new ListResourcesResult
            {
                NextCursor = "embedding-next",
                Resources = Enumerable.Range(0, 120).Select(index => new Resource { Uri = "custom://" + index, Name = index.ToString() }).ToArray()
            });
        });
        await using var client = await fixture.ClientAsync("owner");
        var result = await Raw(client, "resources/list", new() { ["cursor"] = "embedding-cursor" }, fixture.Token);
        Assert.Equal("embedding-next", result["nextCursor"]!.GetValue<string>());
        Assert.Equal(120, result["resources"]!.AsArray().Count);
    }

    private static void AssertUpdate(JsonRpcNotification notification, string uri, string? stream)
    {
        Assert.Equal(NotificationMethods.ResourceUpdatedNotification, notification.Method);
        Assert.Equal(uri, notification.Params!["uri"]!.GetValue<string>());
        Assert.DoesNotContain("private source payload", notification.Params.ToJsonString());
        Assert.Equal(stream == null ? new[] { "uri" } : new[] { "_meta", "uri" }, notification.Params.AsObject().Select(pair => pair.Key).Order().ToArray());
        if (stream != null) Assert.Equal(stream, notification.Params["_meta"]!["io.modelcontextprotocol/subscriptionId"]!.GetValue<string>());
    }
    private static IAsyncDisposable Observe(McpClient client, string method, Channel<JsonRpcNotification> channel) =>
        client.RegisterNotificationHandler(method, (item, _) => { channel.Writer.TryWrite(item); return ValueTask.CompletedTask; });
    private static Task<JsonRpcResponse> Listen(McpClient client, string id, string[] uris, CancellationToken token) =>
        client.SendRequestAsync(new JsonRpcRequest { Id = new(id), Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(new SubscriptionsListenRequestParams { Notifications = new() { ResourceSubscriptions = uris } }) }, token);
    private static Task<JsonNode> Complete(McpClient client, string value, CancellationToken token) => Raw(client, "completion/complete", new()
    {
        ["ref"] = new JsonObject { ["type"] = "ref/resource", ["uri"] = Template },
        ["argument"] = new JsonObject { ["name"] = "path", ["value"] = value }
    }, token);
    private static async Task<JsonNode> Raw(McpClient client, string method, JsonObject parameters, CancellationToken token) =>
        (await client.SendRequestAsync(new JsonRpcRequest { Id = new(Guid.NewGuid().ToString("N")), Method = method, Params = parameters }, token)).Result!;
    public sealed record PathArgs(string Path);

    private sealed class ResourceFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        private WebApplication _server = null!;
        public CancellationToken Token => _timeout.Token;
        public static async Task<ResourceFixture> StartAsync(AutomationCatalog catalog, Action<McpServerOptions>? configure = null)
        {
            var fixture = new ResourceFixture();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer(configure ?? (_ => { }))
                .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients).WithAutomation(catalog);
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
                new() { ProtocolVersion = protocol }, cancellationToken: Token);
        public async ValueTask DisposeAsync()
        { await _server.StopAsync(CancellationToken.None); await _server.DisposeAsync(); _timeout.Dispose(); }
    }
}
