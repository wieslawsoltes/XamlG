using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Responses;

namespace XamlG.Agents.OpenAI;

/// <summary>Official Responses SDK adapter bound to one validated account registration.
/// The output allowance is a local budget estimate: this route rejects max_output_tokens.</summary>
public sealed class ChatGptAccountAgentProvider : IAgentProvider, IAgentProviderSession
{
    public const string ProviderId = "openai-chatgpt";
    private readonly ChatGptAccountManager _accounts;
    private readonly Uri _endpoint;
    internal ChatGptAccountAgentProvider(ChatGptAccountManager accounts, string id, string label, Uri endpoint)
    { _accounts = accounts; AccountId = id; AccountLabel = label; _endpoint = endpoint; }
    public string Id => ProviderId;
    public string AccountId { get; }
    public string AccountLabel { get; }
    /// <summary>Bind the entire host run, including pending approvals and local tools, to the
    /// captured account session. Sign-out/reauthorization cancels it; normal refresh does not.</summary>
    public CancellationToken GetSessionLifetime() => _accounts.SessionLifetime(AccountId);
    public int GetContextBytes(AgentRequest request) => ModelReaderWriter.Write(OpenAIAgentProvider.Options(request, chatGptPlan: true)).ToMemory().Length;
    public async Task<IReadOnlyList<ChatGptModel>> ListModelChoicesAsync(CancellationToken cancellationToken = default)
    {
        try { return await _accounts.ListModelsAsync(AccountId, cancellationToken); }
        catch (ChatGptAccountException error) { _accounts.RecordFailure(AccountId, error.Code, error.Details); throw new AgentProviderException(error.Code, error.Retryable) { Details = error.Details }; }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
        catch (JsonException) { throw new AgentProviderException("invalid_model_catalog", false); }
    }
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        (await ListModelChoicesAsync(cancellationToken)).Select(model => model.Slug).ToArray();
    public async Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken)
    {
        try
        {
            var access = await _accounts.AccessAsync(AccountId, cancellationToken);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, access.Lifetime);
            var client = new ResponsesClient(new ApiKeyCredential(access.Token), new ResponsesClientOptions
            { Endpoint = _endpoint, RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(_accounts.InferenceHttp) });
            return await new OpenAIAgentProvider(client, chatGptPlan: true).GenerateAsync(request, textDelta, lifetime.Token);
        }
        catch (ChatGptAccountException error) { _accounts.RecordFailure(AccountId, error.Code, error.Details); throw new AgentProviderException(error.Code, error.Retryable) { Details = error.Details }; }
        catch (AgentProviderException error) { _accounts.RecordFailure(AccountId, error.Code, error.Details); throw; }
        catch (HttpRequestException) { throw new AgentProviderException("connection_error", true); }
    }
}

internal sealed class ChatGptInferenceHandler : DelegatingHandler
{
    internal ChatGptInferenceHandler() : base(new SocketsHttpHandler { AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;
        using (response)
        {
            var status = (int)response.StatusCode; var code = "http_" + status; AgentProviderFailure? details = null;
            var retryAfter = AgentHttpHandler.ParseRetryAfter(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null);
            try
            {
                var failure = ChatGptOAuthProtocol.Failure(response, await ChatGptOAuthProtocol.ReadBoundedAsync(response.Content, 65536, cancellationToken));
                code = failure.Code; details = failure.Details;
            }
            catch (Exception error) when (error is JsonException or ChatGptAccountException) { }
            var retryable = status is 408 or 429 or >= 500;
            if (code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_user_not_eligible" or "subscription_sharing_unsupported_capability" or
                "subscription_sharing_route_not_supported" or "subscription_sharing_invalid_user" or "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context") retryable = false;
            throw new AgentProviderException(code, retryable, retryAfter, canResume: true) { Details = details };
        }
    }
}
