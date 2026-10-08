using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Responses;
using XamlG.Agents;
using XamlG.Agents.OpenAI;
using XamlG.Automation;
using Xunit;

#pragma warning disable OPENAI001
namespace XamlG.Automation.Tests;

public sealed class OpenAIProviderTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Official_sdk_streaming_preserves_encrypted_reasoning_and_tool_ids_without_public_export(string lineEnding)
    {
        using var handler = new NativeResponsesHandler(lineEnding);
        using var http = new HttpClient(new AgentHttpHandler(handler));
        var client = new ResponsesClient(new ApiKeyCredential("test-only-not-a-secret"), new ResponsesClientOptions
        { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        var provider = new OpenAIAgentProvider(client);
        var catalog = new AutomationCatalog(); var writes = 0;
        catalog.Add<EditArguments, object>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { writes++; return ValueTask.FromResult<object>(new { revision = 1, text = args.Text }); });
        using var harness = new AgentHarness(catalog);
        var task = harness.CreateTask("SDK test", provider, "test-model", TestContext.Current.CancellationToken);
        await harness.RunAsync(task.Id, "Update the source", new() { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, writes);
        Assert.Equal(2, handler.Requests.Count);
        var continuation = handler.Requests[1].GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(continuation, i => i.GetProperty("type").GetString() == "reasoning" && i.GetProperty("encrypted_content").GetString() == "opaque-private-signature");
        Assert.Contains(continuation, i => i.GetProperty("type").GetString() == "function_call_output" && i.GetProperty("call_id").GetString() == "call_1");
        Assert.DoesNotContain("opaque-private-signature", harness.ExportTranscript(task.Id));
        Assert.DoesNotContain("test-only-not-a-secret", harness.ExportTranscript(task.Id));
        Assert.False(handler.Requests[0].GetProperty("store").GetBoolean());
        Assert.Equal(30, task.ReportedTokens);
    }

    public sealed record EditArguments(string Text);
    private sealed class NativeResponsesHandler(string lineEnding) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(body.RootElement.Clone());
            var output = Requests.Count == 1 ? """
                [{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"opaque-private-signature"},
                 {"type":"function_call","id":"fc_1","call_id":"call_1","name":"edit","arguments":"{\"text\":\"updated\"}","status":"completed"}]
                """ : """
                [{"type":"message","id":"msg_2","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Updated and verified.","annotations":[]}]}]
                """;
            var response = "{\"type\":\"response.completed\",\"sequence_number\":0,\"response\":{\"id\":\"resp_" + Requests.Count + "\",\"object\":\"response\",\"created_at\":123,\"model\":\"test-model\",\"status\":\"completed\",\"output\":" + output + ",\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}";
            // Raw string literal line endings follow the checkout on Windows. Serialize the
            // JSON before placing it in an SSE data line; embedded CR characters split frames.
            using var document = JsonDocument.Parse(response);
            var compact = JsonSerializer.Serialize(document.RootElement);
            return new(HttpStatusCode.OK) { Content = new StringContent("event: response.completed" + lineEnding + "data: " + compact + lineEnding + lineEnding, Encoding.UTF8, "text/event-stream") };
        }
    }
}
