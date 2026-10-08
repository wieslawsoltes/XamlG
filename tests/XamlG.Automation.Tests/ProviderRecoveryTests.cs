using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Google.GenAI;
using OpenAI.Responses;
using XamlG.Agents;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using XamlG.Agents.OpenAI;
using XamlG.Automation;
using Xunit;

#pragma warning disable OPENAI001
namespace XamlG.Automation.Tests;

public sealed class ProviderRecoveryTests
{
    private static readonly AgentRequest Request = new("fixture-model", "Help with the project", [new(AgentMessageKind.User, "Edit")], [], 128);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task Quota_code_overrides_rate_status_and_preserves_retry_deadline_without_exporting_body(string provider)
    {
        using var fixture = new Fixture(provider, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"insufficient_quota","type":"rate_limit_error","status":"RESOURCE_EXHAUSTED","message":"private-provider-body"}}""", Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "600"); return response;
        }, sharedTransport: true);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal("quota_exhausted", error.Code); Assert.False(error.Retryable); Assert.True(error.CanResume);
        Assert.Equal(TimeSpan.FromMinutes(10), error.RetryAfter); Assert.Single(fixture.Handler.Requests);
        Assert.DoesNotContain("private-provider-body", error.ToString());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task Injected_sdk_without_shared_handler_still_classifies_structured_quota(string provider)
    {
        using var fixture = new Fixture(provider, _ => new(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("""{"error":{"code":"insufficient_quota","type":"rate_limit_error","message":"private"}}""", Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal("quota_exhausted", error.Code); Assert.False(error.Retryable); Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task Temporary_stream_error_keeps_reported_usage_and_requests_retry(string provider)
    {
        var updates = provider switch
        {
            "openai" => new[] { OpenAiTerminal("failed", error: new { code = "server_error", message = "private-error" }) },
            "anthropic" => new object[]
            {
                AnthropicStart(),
                new { type = "message_delta", delta = new { stop_reason = (string?)null }, usage = new { output_tokens = 7 } },
                new { type = "error", error = new { type = "overloaded_error", message = "private-error" } }
            },
            _ => new object[]
            {
                new { usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 7, totalTokenCount = 17 } },
                new { error = new { code = 503, status = "UNAVAILABLE", message = "private-error" } }
            }
        };
        using var fixture = new Fixture(provider, _ => Stream(updates), sharedTransport: provider == "gemini");
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal("provider_unavailable", error.Code); Assert.True(error.Retryable); Assert.True(error.CanResume);
        Assert.Equal(new AgentUsage(10, 7), error.Usage); Assert.DoesNotContain("private-error", error.ToString());
    }

    [Theory]
    [InlineData("content_filter", null, "safety_rejected", false)]
    [InlineData("max_output_tokens", "content_policy_violation", "safety_rejected", false)]
    [InlineData("max_output_tokens", "insufficient_quota", "quota_exhausted", true)]
    [InlineData("max_output_tokens", "context_length_exceeded", "context_limit", true)]
    [InlineData("unknown_reason", null, "generation_failed", true)]
    public async Task Openai_incomplete_classification_prioritizes_error_over_output_limit(string reason, string? code, string expectedCode, bool canResume)
    {
        using var fixture = new Fixture("openai", _ => Stream(OpenAiTerminal("incomplete", reason, code == null ? null : new { code, message = "private-error" })));
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal(expectedCode, error.Code); Assert.Equal(canResume, error.CanResume); Assert.False(error.Retryable);
        Assert.Equal(new AgentUsage(10, 7), error.Usage);
    }

    [Fact]
    public async Task Openai_stream_error_event_is_classified_without_waiting_for_terminal_response()
    {
        using var fixture = new Fixture("openai", _ => Stream(new { type = "error", sequence_number = 0, code = "rate_limit_exceeded", message = "private-error", param = (string?)null }));
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal("rate_limited", error.Code); Assert.True(error.Retryable); Assert.Null(error.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Openai_output_limit_skips_partial_calls_and_only_requires_a_larger_cap_when_supported(bool accountMode)
    {
        using var fixture = new Fixture("openai", count => Stream(count == 1 ? OpenAiTerminal("incomplete", "max_output_tokens", output:
            [new { type = "function_call", id = "fc_1", call_id = "call_1", name = "edit", arguments = "{\"text\":", status = "incomplete", @namespace = "xamlg" }]) :
            OpenAiTerminal("completed")), accountMode: accountMode);
        using var harness = new AgentHarness(new AutomationCatalog());
        var task = harness.CreateTask("limited generation", fixture.Provider, "fixture-model", Token);
        var options = new AgentRunOptions { Limits = new() { AutomaticRetries = 0, OutputTokensPerRequest = 128 } };
        await harness.RunAsync(task.Id, "Edit", options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Paused, task.Status); Assert.Equal(17, task.ReportedTokens);
        if (accountMode) Assert.Null(task.OutputLimitToExceed);
        else
        {
            Assert.Equal(128, task.OutputLimitToExceed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(task.Id, null, options, cancellationToken: Token));
            options = options with { Limits = options.Limits with { OutputTokensPerRequest = 256 } };
        }
        await harness.RunAsync(task.Id, null, options, cancellationToken: Token);
        Assert.Equal(AgentTaskStatus.Completed, task.Status); Assert.Equal(34, task.ReportedTokens);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Single(fixture.Handler.Requests[1].GetProperty("input").EnumerateArray());
        Assert.Equal(!accountMode, fixture.Handler.Requests[0].TryGetProperty("max_output_tokens", out _));
    }

    [Theory]
    [InlineData("content_policy_violation", "prompt is too long: private details", "safety_rejected", false)]
    [InlineData("invalid_request_error", "prompt is too long: private details", "context_limit", true)]
    [InlineData("unrecognized_error", "an arbitrary context_length_exceeded string", "http_400", true)]
    public async Task Failure_priority_uses_exact_codes_and_anchored_context_messages(string code, string message, string expected, bool canResume)
    {
        using var fixture = new Fixture("openai", _ => new(HttpStatusCode.BadRequest)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message } }), Encoding.UTF8, "application/json") }, sharedTransport: true);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal(expected, error.Code); Assert.Equal(canResume, error.CanResume); Assert.DoesNotContain(message, error.ToString());
    }

    [Theory]
    [InlineData("SAFETY", "safety_rejected")]
    [InlineData("RECITATION", "safety_rejected")]
    [InlineData("MALFORMED_FUNCTION_CALL", "invalid_tool_arguments")]
    [InlineData("TOO_MANY_TOOL_CALLS", "tool_call_limit")]
    public async Task Gemini_rejection_preserves_usage_and_never_returns_function_calls(string reason, string expected)
    {
        using var fixture = new Fixture("gemini", _ => Stream(new
        {
            candidates = new[] { new { index = 0, finishReason = reason, content = new { role = "model", parts = new[] { new { functionCall = new { name = "edit", args = new { } } } } } } },
            usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 5, thoughtsTokenCount = 2, totalTokenCount = 17 }
        }));
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => fixture.Provider.GenerateAsync(Request, _ => ValueTask.CompletedTask, Token));
        Assert.Equal(expected, error.Code); Assert.False(error.CanResume); Assert.Equal(new AgentUsage(10, 7), error.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gemini_error_after_a_completed_candidate_never_executes_its_tools(bool sharedTransport)
    {
        using var fixture = new Fixture("gemini", _ => Stream(
            new { candidates = new[] { new { index = 0, finishReason = "STOP", content = new { role = "model", parts = new[] { new { functionCall = new { name = "edit", args = new { text = "changed" } } } } } } },
                usageMetadata = new { promptTokenCount = 10, candidatesTokenCount = 7, totalTokenCount = 17 } },
            new { error = new { code = "content_policy_violation", message = "private-safety-error" } }), sharedTransport: sharedTransport);
        var catalog = new AutomationCatalog(); var writes = 0;
        catalog.Add<EditArguments, object>("edit", "Edit source", AutomationScope.Source, AutomationEffect.Edit,
            (_, _) => { writes++; return ValueTask.FromResult<object>(new { revision = 1 }); });
        using var harness = new AgentHarness(catalog);
        var task = harness.CreateTask("rejected generation", fixture.Provider, "fixture-model", Token);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => harness.RunAsync(task.Id, "Edit", new() { Policy = new() { Profile = PermissionProfile.AutoEdit } }, cancellationToken: Token));
        Assert.Equal(sharedTransport ? "safety_rejected" : "invalid_response_protocol", error.Code);
        Assert.Equal(0, writes); Assert.Equal(AgentTaskStatus.Failed, task.Status); Assert.Equal(17, task.ReportedTokens);
        Assert.DoesNotContain("private-safety-error", harness.ExportTranscript(task.Id));
    }

    public sealed record EditArguments(string Text);

    private static object OpenAiTerminal(string status, string? reason = null, object? error = null, object[]? output = null) => new
    {
        type = "response." + status, sequence_number = 1,
        response = new { id = "resp_fixture", @object = "response", created_at = 123, model = "fixture-model", status, error,
            incomplete_details = reason == null ? null : new { reason }, output = output ?? [], usage = new { input_tokens = 10, output_tokens = 7, total_tokens = 17 } }
    };
    private static object AnthropicStart() => new
    {
        type = "message_start", message = new { id = "msg_fixture", type = "message", role = "assistant", model = "fixture-model", content = Array.Empty<object>(),
            stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 10, output_tokens = 0 } }
    };
    private static HttpResponseMessage Stream(params object[] updates)
    {
        var body = new StringBuilder();
        foreach (var update in updates)
        {
            var data = JsonSerializer.SerializeToElement(update);
            if (data.TryGetProperty("type", out var type)) body.Append("event: ").Append(type.GetString()).Append('\n');
            body.Append("data: ").Append(data.GetRawText()).Append("\n\n");
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly AnthropicClient? _anthropic;
        private readonly Client? _google;
        internal Handler Handler { get; }
        internal IAgentProvider Provider { get; }
        internal Fixture(string provider, Func<int, HttpResponseMessage> response, bool sharedTransport = false, bool accountMode = false)
        {
            Handler = new(response); _http = new(sharedTransport ? new AgentHttpHandler(Handler) : Handler);
            if (provider == "openai") Provider = new OpenAIAgentProvider(new ResponsesClient(new ApiKeyCredential("fixture-only"), new ResponsesClientOptions
            { Transport = new HttpClientPipelineTransport(_http), RetryPolicy = new ClientRetryPolicy(0) }), chatGptPlan: accountMode);
            else if (provider == "anthropic")
            { _anthropic = new() { ApiKey = "fixture-only", HttpClient = _http, MaxRetries = 0 }; Provider = new AnthropicAgentProvider(_anthropic); }
            else
            {
                _google = new(enterprise: false, apiKey: "fixture-only", httpOptions: new() { RetryOptions = new() { Attempts = 1 } }, clientOptions: new() { HttpClientFactory = () => _http });
                Provider = new GeminiAgentProvider(_google);
            }
        }
        public void Dispose() { _anthropic?.Dispose(); _google?.Dispose(); _http.Dispose(); }
    }
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(body.RootElement.Clone()); return response(Requests.Count);
        }
    }
}
