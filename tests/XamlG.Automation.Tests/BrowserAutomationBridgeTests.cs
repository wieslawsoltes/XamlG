using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using XamlG.Automation;
using XamlG.Mcp;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class BrowserAutomationBridgeTests
{
    [Fact]
    public async Task Pairing_lease_expires_and_pending_calls_cannot_follow_a_replacement_browser()
    {
        var bridge = new BrowserAutomationBridge();
        // An observer cannot strand the transport during attach or disconnect.
        bridge.CatalogChanged += () => throw new InvalidOperationException("observer");
        await using var first = await Connection.CreateAsync(bridge);
        Assert.True(bridge.TryGetOwnerSession(first.Lease, out var lifetime));
        Assert.False(bridge.TryGetOwnerSession("wrong", out _));
        var pending = bridge.CallAsync("echo", AutomationJson.Element(new { text = "old" }), new("AI Agent", first.Token)).AsTask();
        var request = await first.ReceiveAsync();
        Assert.Equal("AI Agent", request.GetProperty("caller").GetString());
        await first.DisconnectAsync();
        Assert.True(lifetime.IsCancellationRequested);
        Assert.False(bridge.TryGetOwnerSession(first.Lease, out _));
        Assert.Equal("disconnected", (await Assert.ThrowsAsync<AutomationException>(() => pending)).Code);
        Assert.Empty(bridge.Tools);

        await using var second = await Connection.CreateAsync(bridge);
        Assert.NotEqual(first.Lease, second.Lease);
        Assert.False(bridge.TryGetOwnerSession(first.Lease, out _));
        Assert.True(bridge.TryGetOwnerSession(second.Lease, out _));
        var current = bridge.CallAsync("echo", AutomationJson.Element(new { text = "new" }), new("MCP", second.Token)).AsTask();
        var next = await second.ReceiveAsync();
        Assert.True(next.GetProperty("id").GetInt64() > request.GetProperty("id").GetInt64());
        Assert.Equal("new", next.GetProperty("arguments").GetProperty("text").GetString());
        await second.SendAsync(new { id = next.GetProperty("id").GetInt64(), result = new { text = "new" } });
        Assert.Equal("new", (await current).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Concurrent_calls_reserve_the_bounded_queue_atomically()
    {
        var bridge = new BrowserAutomationBridge();
        await using var connection = await Connection.CreateAsync(bridge);
        var calls = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
        {
            try { return await bridge.CallAsync("echo", AutomationJson.Element(new { text = index.ToString() }), new("test", connection.Token)); }
            catch (AutomationException error) when (error.Code == "busy") { return AutomationJson.Element(new { busy = true }); }
        })).ToArray();
        var admitted = new List<long>();
        for (var index = 0; index < 32; index++) admitted.Add((await connection.ReceiveAsync()).GetProperty("id").GetInt64());
        // Observe every remaining attempt rejected before releasing any reserved slot.
        while (calls.Count(t => t.IsCompleted) < 32) await Task.Delay(1, connection.Token);
        Assert.Equal(32, calls.Count(t => t.IsCompletedSuccessfully));
        foreach (var id in admitted) await connection.SendAsync(new { id, result = new { accepted = true } });
        var results = await Task.WhenAll(calls);
        Assert.Equal(32, results.Count(r => r.TryGetProperty("busy", out _)));
        Assert.Equal(32, results.Count(r => r.TryGetProperty("accepted", out _)));
    }

    [Fact]
    public async Task Cancellation_is_forwarded_and_late_results_do_not_complete_another_call()
    {
        var bridge = new BrowserAutomationBridge();
        await using var connection = await Connection.CreateAsync(bridge);
        using var cancel = new CancellationTokenSource();
        var pending = bridge.CallAsync("echo", AutomationJson.Element(new { text = "cancel" }), new("test", cancel.Token)).AsTask();
        var request = await connection.ReceiveAsync();
        cancel.Cancel();
        var cancellation = await connection.ReceiveAsync();
        Assert.Equal("cancel", cancellation.GetProperty("kind").GetString());
        Assert.Equal(request.GetProperty("id").GetInt64(), cancellation.GetProperty("id").GetInt64());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await connection.SendAsync(new { id = request.GetProperty("id").GetInt64(), result = new { text = "late" } });
        var next = bridge.CallAsync("echo", AutomationJson.Element(new { text = "next" }), new("test", connection.Token)).AsTask();
        var current = await connection.ReceiveAsync();
        await connection.SendAsync(new { id = current.GetProperty("id").GetInt64(), result = new { text = "current" } });
        Assert.Equal("current", (await next).GetProperty("text").GetString());
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(20));
        private readonly TcpClient _browserTcp, _serverTcp;
        private readonly WebSocket _browser, _server;
        private readonly Task _run;
        public string Lease { get; private set; } = "";
        public CancellationToken Token => _timeout.Token;

        private Connection(BrowserAutomationBridge bridge, TcpClient browser, TcpClient server)
        {
            _browserTcp = browser; _serverTcp = server;
            _browser = WebSocket.CreateFromStream(browser.GetStream(), false, null, Timeout.InfiniteTimeSpan);
            _server = WebSocket.CreateFromStream(server.GetStream(), true, null, Timeout.InfiniteTimeSpan);
            var catalog = new AutomationCatalog();
            catalog.Add<Echo, Echo>("echo", "Echo", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
            _run = bridge.RunAsync(_server, new(catalog.Tools, catalog.Resources, catalog.Prompts), Token);
        }
        public static async Task<Connection> CreateAsync(BrowserAutomationBridge bridge)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var browser = new TcpClient();
            var connect = browser.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            await connect;
            var connection = new Connection(bridge, browser, server);
            connection.Lease = (await connection.ReceiveAsync()).GetProperty("ownerSession").GetString()!;
            return connection;
        }
        public async Task<JsonElement> ReceiveAsync() => (await BrowserAutomationBridge.ReceiveAsync(_browser, Token))!.Value;
        public async Task SendAsync<T>(T message) => await _browser.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, Token);
        public async Task DisconnectAsync()
        {
            await _browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", Token);
            await _run.WaitAsync(Token);
        }
        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel(); _browser.Abort(); _server.Abort();
            try { await _run; } catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
            _browser.Dispose(); _server.Dispose(); _browserTcp.Dispose(); _serverTcp.Dispose(); _timeout.Dispose();
        }
    }
    private sealed record Echo(string Text);
}
