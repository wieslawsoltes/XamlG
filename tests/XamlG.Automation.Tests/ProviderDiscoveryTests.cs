using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Google.GenAI;
using XamlG.Agents;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using Xunit;

namespace XamlG.Automation.Tests;

public sealed class ProviderDiscoveryTests
{
    [Theory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task Model_discovery_follows_official_sdk_pagination(string id)
    {
        using var handler = new DiscoveryHandler(id);
        using var http = new HttpClient(handler);
        using var anthropic = new AnthropicClient { ApiKey = "test-key", HttpClient = http };
        using var google = new Client(enterprise: false, apiKey: "test-key", clientOptions: new() { HttpClientFactory = () => http });
        IAgentProvider provider = id == "anthropic" ? new AnthropicAgentProvider(anthropic) : new GeminiAgentProvider(google);
        var models = await provider.ListModelsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(id == "anthropic" ? ["first", "second"] : new[] { "models/first", "models/second" }, models);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task Discovery_errors_are_sanitized_and_sdk_retries_are_disabled(string id)
    {
        using var handler = new DiscoveryHandler(id, fail: true);
        using var http = new HttpClient(handler);
        using var anthropic = new AnthropicClient { ApiKey = "test-key", HttpClient = http };
        using var google = new Client(enterprise: false, apiKey: "test-key", clientOptions: new() { HttpClientFactory = () => http });
        IAgentProvider provider = id == "anthropic" ? new AnthropicAgentProvider(anthropic) : new GeminiAgentProvider(google);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => provider.ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("http_503", error.Code); Assert.True(error.Retryable); Assert.Single(new int[handler.Calls]);
        Assert.DoesNotContain("private-upstream-body", error.ToString());
    }

    private sealed class DiscoveryHandler(string provider, bool fail = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"error\":{\"code\":503,\"type\":\"overloaded_error\",\"message\":\"private-upstream-body\",\"status\":\"UNAVAILABLE\"}}", Encoding.UTF8, "application/json") });
            if (Calls == 2) Assert.Contains(provider == "anthropic" ? "after_id=first" : "pageToken=next", request.RequestUri!.Query);
            object response = provider == "anthropic" ? new { data = new[] { new { id = Calls == 1 ? "first" : "second", type = "model", display_name = "Fixture", created_at = "2026-01-01T00:00:00Z" } }, has_more = Calls == 1, first_id = Calls == 1 ? "first" : "second", last_id = Calls == 1 ? "first" : "second" } :
                new { models = new[] { new { name = Calls == 1 ? "models/first" : "models/second" } }, nextPageToken = Calls == 1 ? "next" : null };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
