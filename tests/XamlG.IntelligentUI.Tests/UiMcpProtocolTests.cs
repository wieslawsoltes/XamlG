using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XamlG.Automation;
using XamlG.Mcp;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiMcpProtocolTests
{
    [Fact]
    public async Task OfficialMcpClientReceivesUiMetadataResourcesFallbackAndRevisionCheckedState()
    {
        var catalog = new AutomationCatalog(); var store = new UiSessionStore();
        using var registration = new UiAutomation(catalog, store);
        var inbound = new Pipe(); var outbound = new Pipe();
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStreamServerTransport(inbound.Reader.AsStream(), outbound.Writer.AsStream())
            .WithAutomation(catalog).WithAutomationUi();
        using var server = builder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await server.StartAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(inbound.Writer.AsStream(), outbound.Reader.AsStream()), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var present = Assert.Single(tools, tool => tool.Name == "xamlg_ui_present");
            Assert.Equal(UiAutomation.ResourceUri, present.ProtocolTool.Meta!["ui"]!["resourceUri"]!.GetValue<string>());
            var args = AutomationJson.Element(UiExamples.Pricing()).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
            var result = await client.CallToolAsync("xamlg_ui_present", args, cancellationToken: timeout.Token);
            Assert.False(result.IsError == true);
            Assert.Contains("$232", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(UiPresentation.TryRead(result.StructuredContent!.Value.GetRawText(), out var marker));
            var read = await client.CallToolAsync("xamlg_ui_read", new Dictionary<string, object?> { ["id"] = marker!.Id }, cancellationToken: timeout.Token);
            Assert.Equal(marker.SessionId, read.StructuredContent!.Value.GetProperty("sessionId").GetString());
            var resource = await client.ReadResourceAsync(UiAutomation.ResourceUri, cancellationToken: timeout.Token);
            var contents = Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents));
            Assert.Equal(UiAutomation.MimeType, contents.MimeType);
            Assert.Contains("ui/initialize", contents.Text);
            Assert.NotNull(contents.Meta!["ui"]!["csp"]);
            var stateArgs = new Dictionary<string, object?> { ["id"] = marker.Id, ["expectedRevision"] = marker.Revision, ["expectedStateRevision"] = marker.StateRevision, ["key"] = "seats", ["value"] = 11 };
            var changed = await client.CallToolAsync("xamlg_ui_state", stateArgs, cancellationToken: timeout.Token);
            Assert.False(changed.IsError == true);
            Assert.Contains("$319", changed.StructuredContent!.Value.GetProperty("fallbackMarkdown").GetString());
            var stale = await client.CallToolAsync("xamlg_ui_state", stateArgs, cancellationToken: timeout.Token);
            Assert.True(stale.IsError);
            Assert.Equal("revision_conflict", stale.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        }
        finally { await server.StopAsync(TestContext.Current.CancellationToken); }
    }
}
