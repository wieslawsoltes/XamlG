using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using XamlG.Automation;
using XamlG.Mcp;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class McpProtocolTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Official_client_discovers_invokes_and_reads_catalog_through_real_protocol()
    {
        var catalog = new AutomationCatalog();
        catalog.Add<EchoArguments, EchoArguments>("echo", "Echo structured arguments", AutomationScope.Source,
            AutomationEffect.Read, (args, _) => ValueTask.FromResult(args));
        catalog.AddResource(new("xamlg://test", "Test", "Test resource"), _ => ValueTask.FromResult("{\"value\":42}"));
        catalog.AddPrompt(new("inspect", "Inspect", "Inspect the current project."));
        var inbound = new Pipe(); var outbound = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddProvider(new TestLogProvider(output));
        builder.Services.AddMcpServer().WithStreamServerTransport(inbound.Reader.AsStream(), outbound.Writer.AsStream()).WithAutomation(catalog);
        using var server = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await server.StartAsync(timeout.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(inbound.Writer.AsStream(), outbound.Reader.AsStream()), cancellationToken: timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Equal("echo", Assert.Single(tools).Name);
            var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "actual protocol" }, cancellationToken: timeout.Token);
            Assert.False(result.IsError == true);
            Assert.Equal("actual protocol", result.StructuredContent!.Value.GetProperty("text").GetString());
            var invalid = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = 12 }, cancellationToken: timeout.Token);
            Assert.True(invalid.IsError);
            var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
            Assert.Equal("xamlg://test", Assert.Single(resources).Uri);
            var resource = await client.ReadResourceAsync("xamlg://test", cancellationToken: timeout.Token);
            Assert.Equal("{\"value\":42}", Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
            var prompts = await client.ListPromptsAsync(cancellationToken: timeout.Token);
            Assert.Equal("inspect", Assert.Single(prompts).Name);
        }
        finally { await server.StopAsync(timeout.Token); }
    }

    public sealed record EchoArguments(string Text);
    private sealed class TestLogProvider(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestLog(output);
        public void Dispose() { }
    }
    private sealed class TestLog(ITestOutputHelper output) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) output.WriteLine(formatter(state, exception) + "\n" + exception); }
    }
}
