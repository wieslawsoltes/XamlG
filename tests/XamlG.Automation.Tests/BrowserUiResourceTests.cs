using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XamlG.Automation;
using XamlG.Mcp;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class BrowserUiResourceTests
{
    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task HttpMcpRetainsBrowserResourceMimeAndSecurityMetadata(string protocol)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30)); var token = lifetime.Token;
        var bridge = new BrowserAutomationBridge();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithAutomation(bridge).WithAutomationUi();
        await using var host = builder.Build(); host.UseWebSockets(); host.MapMcp("/mcp");
        host.Map("/bridge", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var packet = await ReadAsync(socket, token);
            await bridge.RunAsync(socket, packet.Deserialize<BrowserCatalog>(AutomationJson.Options)!, token);
        });
        await host.StartAsync(token);
        using var browser = new ClientWebSocket();
        try
        {
            await browser.ConnectAsync(new Uri(host.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal) + "/bridge"), token);
            var catalog = new AutomationCatalog();
            catalog.Add<Empty, Empty>("echo", "Echo", AutomationScope.Source, AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
            const string uri = "ui://test/native";
            catalog.AddResource(new(uri, "Native", "Native resource", "text/html;profile=mcp-app",
                Metadata: AutomationJson.Element(new { ui = new { csp = new { frameDomains = new[] { "https://example.test" } } } })), _ => ValueTask.FromResult("<html>Native</html>"));
            await SendAsync(browser, new BrowserCatalog(catalog.Tools, catalog.Resources, catalog.Prompts, "workspace-identity"), token);
            var lease = await ReadAsync(browser, token); Assert.Equal("ownerLease", lease.GetProperty("kind").GetString());
            Assert.Equal("text/html;profile=mcp-app", Assert.Single(bridge.Resources).MimeType);
            Assert.NotNull(Assert.Single(bridge.Resources).Metadata);
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(host.Urls.Single() + "/mcp") }),
                new() { ProtocolVersion = protocol }, cancellationToken: token);
            var resources = await client.ListResourcesAsync(cancellationToken: token);
            var resource = Assert.Single(resources);
            Assert.Equal("text/html;profile=mcp-app", resource.MimeType);
            var read = client.ReadResourceAsync(uri, cancellationToken: token);
            var call = await ReadAsync(browser, token);
            Assert.Equal("resource", call.GetProperty("method").GetString());
            await SendAsync(browser, new { id = call.GetProperty("id").GetString(), result = "<html>Native</html>" }, token);
            var content = Assert.IsType<TextResourceContents>(Assert.Single((await read).Contents));
            Assert.Equal("text/html;profile=mcp-app", content.MimeType);
            Assert.Equal("https://example.test", content.Meta!["ui"]!["csp"]!["frameDomains"]![0]!.GetValue<string>());
            Assert.Equal("<html>Native</html>", content.Text);
        }
        finally
        {
            if (browser.State == WebSocketState.Open) await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
    private static async Task SendAsync(WebSocket socket, object value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, AutomationJson.Options));
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
    }
    private static async Task<JsonElement> ReadAsync(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[32768]; var used = 0;
        while (true)
        {
            if (used == bytes.Length) throw new InvalidOperationException("Test frame too large.");
            var result = await socket.ReceiveAsync(bytes.AsMemory(used), token); used += result.Count;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Expected JSON text.");
            if (!result.EndOfMessage) continue;
            using var json = JsonDocument.Parse(bytes.AsMemory(0, used)); return json.RootElement.Clone();
        }
    }
    public sealed record Empty;
}
