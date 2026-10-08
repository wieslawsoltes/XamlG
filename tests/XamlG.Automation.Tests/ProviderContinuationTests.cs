using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Google.GenAI;
using Google.GenAI.Types;
using XamlG.Agents;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using XamlG.Automation;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class ProviderContinuationTests
{
    [Theory]
    [InlineData("anthropic", true)]
    [InlineData("gemini", true)]
    [InlineData("gemini", false)]
    public async Task Official_sdk_native_continuations_preserve_private_content_and_match_tool_results(string id, bool nativeId)
    {
        using var handler = new ProviderHandler(id, nativeId);
        using var http = new HttpClient(new AgentHttpHandler(handler));
        using var google = Google(http);
        using var anthropic = new AnthropicClient { ApiKey = "test-provider-key", HttpClient = http, MaxRetries = 0 };
        IAgentProvider provider = id == "anthropic" ? new AnthropicAgentProvider(anthropic) : new GeminiAgentProvider(google);
        var catalog = new AutomationCatalog(); var writes = 0;
        catalog.Add<EditArguments, object>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (args, _) => { writes++; return ValueTask.FromResult<object>(new { text = args.Text, revision = writes }); });
        using var harness = new AgentHarness(catalog);
        var task = harness.CreateTask("provider continuation", provider, "fixture-model");
        var deltas = new StringBuilder();
        harness.EventPublished += item => { if (item.Kind == "text_delta") deltas.Append(item.Text); };
        await harness.RunAsync(task.Id, "Edit and verify", new() { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(1, writes); Assert.Equal(2, handler.Requests.Count);
        var continuation = handler.Requests[1].GetRawText();
        Assert.Contains(id == "anthropic" ? "signed-private-thinking" : Convert.ToBase64String(Encoding.UTF8.GetBytes("signed-private-thinking")), continuation);
        Assert.Contains("private-deliberation", continuation);
        Assert.Contains(id == "anthropic" ? "tool_result" : "functionResponse", continuation);
        Assert.Contains("updated", continuation);
        if (id == "anthropic")
        {
            var messages = handler.Requests[1].GetProperty("messages");
            Assert.Equal("call-fixture", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
            Assert.Equal("opaque-redacted", messages[1].GetProperty("content")[1].GetProperty("data").GetString());
            Assert.Equal(36, task.ReportedTokens); // Input, cache reads/writes and output, for both requests.
        }
        else
        {
            var response = handler.Requests[1].GetProperty("contents")[2].GetProperty("parts")[0].GetProperty("functionResponse");
            Assert.Equal("edit", response.GetProperty("name").GetString());
            Assert.Equal(nativeId, response.TryGetProperty("id", out var responseId));
            if (nativeId) Assert.Equal("call-fixture", responseId.GetString());
            Assert.Equal(30, task.ReportedTokens);
        }
        var transcript = harness.ExportTranscript(task.Id);
        Assert.Contains("Updated and verified.", transcript);
        Assert.DoesNotContain("private-deliberation", transcript);
        Assert.DoesNotContain("signed-private-thinking", transcript);
        Assert.DoesNotContain("opaque-redacted", transcript);
        Assert.DoesNotContain("test-provider-key", transcript);
        Assert.Contains("Updated and verified.", deltas.ToString());
    }

    [Theory]
    [InlineData("anthropic", "malformed", AgentTaskStatus.Failed)]
    [InlineData("gemini", "malformed", AgentTaskStatus.Failed)]
    [InlineData("anthropic", "truncated", AgentTaskStatus.Paused)]
    [InlineData("gemini", "truncated", AgentTaskStatus.Paused)]
    [InlineData("anthropic", "output_limit", AgentTaskStatus.Paused)]
    [InlineData("gemini", "output_limit", AgentTaskStatus.Paused)]
    [InlineData("anthropic", "refusal", AgentTaskStatus.Failed)]
    [InlineData("gemini", "refusal", AgentTaskStatus.Failed)]
    public async Task Incomplete_or_invalid_streams_never_execute_partial_tools(string id, string mode, AgentTaskStatus expected)
    {
        using var handler = new ProviderHandler(id, true, mode);
        using var http = new HttpClient(new AgentHttpHandler(handler));
        using var google = Google(http);
        using var anthropic = new AnthropicClient { ApiKey = "test-provider-key", HttpClient = http, MaxRetries = 0 };
        IAgentProvider provider = id == "anthropic" ? new AnthropicAgentProvider(anthropic) : new GeminiAgentProvider(google);
        var catalog = new AutomationCatalog(); var writes = 0;
        catalog.Add<EditArguments, object>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => { writes++; return ValueTask.FromResult<object>(new { changed = true }); });
        using var harness = new AgentHarness(catalog);
        var task = harness.CreateTask("incomplete response", provider, "fixture-model");
        var options = new AgentRunOptions { Policy = new() { Profile = PermissionProfile.FullAccess }, Limits = new() { AutomaticRetries = 0 } };
        if (expected == AgentTaskStatus.Failed)
            await Assert.ThrowsAsync<AgentProviderException>(() => harness.RunAsync(task.Id, "Edit", options, cancellationToken: TestContext.Current.CancellationToken));
        else await harness.RunAsync(task.Id, "Edit", options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, task.Status); Assert.Equal(0, writes); Assert.Single(handler.Requests);
        if (expected == AgentTaskStatus.Paused)
        {
            if (mode == "output_limit") options = options with { Limits = options.Limits with { OutputTokensPerRequest = options.Limits.OutputTokensPerRequest + 1024 } };
            if (task.RetryAfterUtc is { } deadline && deadline > DateTimeOffset.UtcNow) await Task.Delay(deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            await harness.RunAsync(task.Id, null, options, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(0, writes);
            var history = handler.Requests[1].GetProperty(id == "anthropic" ? "messages" : "contents");
            Assert.Equal(1, history.GetArrayLength());
        }
    }

    private static Client Google(HttpClient http) => new(enterprise: false, apiKey: "test-provider-key",
        httpOptions: new() { RetryOptions = new() { Attempts = 1 } }, clientOptions: new() { HttpClientFactory = () => http });
    public sealed record EditArguments(string Text);

    private sealed class ProviderHandler(string provider, bool nativeId, string mode = "normal") : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(body.RootElement.Clone());
            var events = provider == "anthropic" ? AnthropicEvents(Requests.Count == 1, mode) : GeminiEvents(Requests.Count == 1, mode, nativeId);
            var content = string.Join("", events.Select(item => "data: " + JsonSerializer.Serialize(item) + "\r\n\r\n"));
            return new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/event-stream") };
        }

        private static IEnumerable<object> AnthropicEvents(bool first, string mode)
        {
            yield return new { type = "message_start", message = new { id = "fixture", type = "message", role = "assistant", model = "fixture-model", content = Array.Empty<object>(), stop_reason = (string?)null,
                stop_sequence = (string?)null, usage = new { input_tokens = 10, cache_read_input_tokens = 2, cache_creation_input_tokens = 1, output_tokens = 0 } } };
            if (first)
            {
                yield return new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } };
                yield return new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "private-deliberation" } };
                yield return new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "signed-private-thinking" } };
                yield return new { type = "content_block_stop", index = 0 };
                yield return new { type = "content_block_start", index = 1, content_block = new { type = "redacted_thinking", data = "opaque-redacted" } };
                yield return new { type = "content_block_stop", index = 1 };
                yield return new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "call-fixture", name = "edit", input = new { } } };
                yield return new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = mode is "malformed" or "output_limit" ? "{\"text\":" : "{\"text\":\"updated\"}" } };
                yield return new { type = "content_block_stop", index = 2 };
            }
            else
            {
                yield return new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } };
                yield return new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Updated and verified." } };
                yield return new { type = "content_block_stop", index = 0 };
            }
            if (first && mode == "truncated") yield break;
            yield return new { type = "message_delta", delta = new { stop_reason = first ? mode == "refusal" ? "refusal" : mode == "output_limit" ? "max_tokens" : "tool_use" : "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5 } };
            yield return new { type = "message_stop" };
        }

        private static IEnumerable<object> GeminiEvents(bool first, string mode, bool nativeId)
        {
            var parts = new List<object>();
            if (first)
            {
                parts.Add(new { thought = true, text = "private-deliberation", thoughtSignature = Convert.ToBase64String(Encoding.UTF8.GetBytes("signed-private-thinking")) });
                var call = new Dictionary<string, object> { ["name"] = "edit", ["args"] = new { text = "updated" } };
                if (nativeId) call["id"] = "call-fixture";
                parts.Add(new { functionCall = call, thoughtSignature = Convert.ToBase64String(Encoding.UTF8.GetBytes("tool-signature")) });
            }
            else parts.Add(new { text = "Updated and verified." });
            var candidate = new Dictionary<string, object> { ["index"] = 0, ["content"] = new { role = "model", parts } };
            if (!first || mode != "truncated") candidate["finishReason"] = first && mode == "refusal" ? "SAFETY" : first && mode == "malformed" ? "MALFORMED_FUNCTION_CALL" : first && mode == "output_limit" ? "MAX_TOKENS" : "STOP";
            yield return new { candidates = new[] { candidate }, usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 3, thoughtsTokenCount = 2, totalTokenCount = 15 } };
        }
    }
}
