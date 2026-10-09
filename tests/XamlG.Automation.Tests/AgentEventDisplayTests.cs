using System.Text.Json;
using XamlG.Agents;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class AgentEventDisplayTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(60)]
    public async Task Tool_results_have_correlated_names_and_bounded_structured_previews_without_changing_provider_history(int nodeCount)
    {
        var host = new AutomationCatalog();
        host.Add<ReadArgs, object>("xamlg_runtime_tree", "Read runtime tree", AutomationScope.Runtime, AutomationEffect.Read,
            (_, _) => ValueTask.FromResult<object>(new { revision = 89, nodes = Enumerable.Range(0, nodeCount)
                .Select(index => new { name = "Node " + index, type = "TextBlock", text = "<img onerror=alert(1)>" + new string('x', 300) }).ToArray() }));
        var provider = new ScriptedAgentProvider();
        provider.Add(ScriptedAgentProvider.Call(new AgentToolCall("read-tree", "xamlg_runtime_tree", AutomationJson.Element(new ReadArgs()))));
        provider.Add(ScriptedAgentProvider.Done());
        using var session = new AgentWorkbenchSession(host, [provider]);
        var task = session.Harness.CreateTask("Inspect", provider, "fixture", TestContext.Current.CancellationToken);
        await session.Harness.RunAsync(task.Id, "Inspect the runtime", new(), cancellationToken: TestContext.Current.CancellationToken);

        var raw = Assert.Single(provider.Requests[1].Messages, message => message.Kind == AgentMessageKind.ToolResult).Text;
        using var original = JsonDocument.Parse(raw);
        Assert.Equal(nodeCount, original.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Contains("Node " + (nodeCount - 1), session.Harness.ExportTranscript(task.Id));

        var state = await session.ExecuteAsync("state", AutomationJson.Element(new { }), TestContext.Current.CancellationToken);
        var thread = await session.ExecuteAsync("thread", AutomationJson.Element(new { id = task.Id }), TestContext.Current.CancellationToken);
        var activity = await session.ExecuteAsync("activity", AutomationJson.Element(new { }), TestContext.Current.CancellationToken);
        foreach (var events in new[] { state.GetProperty("tasks")[0].GetProperty("events"), thread.GetProperty("events"), activity })
        {
            var started = events.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "tool_started");
            var completed = events.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "tool_completed");
            Assert.Equal("read-tree", completed.GetProperty("toolCallId").GetString());
            Assert.Equal(started.GetProperty("toolName").GetString(), completed.GetProperty("toolName").GetString());
            Assert.Equal("xamlg_runtime_tree", completed.GetProperty("toolName").GetString());
            if (nodeCount == 60)
            {
                var preview = completed.GetProperty("resultPreview");
                Assert.Equal(89, preview.GetProperty("revision").GetInt32());
                var nodes = preview.GetProperty("nodes");
                Assert.Equal(nodeCount, nodes.GetArrayLength() - 1 + nodes[nodes.GetArrayLength() - 1].GetProperty("$moreItems").GetInt32());
                Assert.True(preview.GetRawText().Length < 8192);
                Assert.True(completed.GetProperty("text").GetString()!.Length < raw.Length);
            }
            else Assert.Equal(raw, completed.GetProperty("text").GetString());
        }
    }

    public sealed record ReadArgs;
}
