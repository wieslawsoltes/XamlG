using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Responses;

namespace XamlG.Agents.OpenAI;

/// <summary>Official Responses SDK adapter bound to one validated account registration.
/// The output allowance is a local budget estimate: this route rejects max_output_tokens.</summary>
public sealed class ChatGptAccountAgentProvider : IAgentProvider, IAgentProviderSession, IAgentProviderState
{
    public string? AccountIdentity => AccountId;
    public JsonElement SaveNative(object native) => OpenAIAgentProvider.SaveContinuation(native);
    public object RestoreNative(JsonElement native) => OpenAIAgentProvider.RestoreContinuation(native);
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
            var status = (int)response.StatusCode; AgentProviderFailure? details = null;
            var retryAfter = AgentHttpHandler.ParseRetryAfter(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null,
                response.Headers.TryGetValues("retry-after-ms", out var milliseconds) ? milliseconds.FirstOrDefault() : null);
            var classified = AgentProviderErrors.FromCode(null, status, retryAfter: retryAfter);
            try
            {
                var bytes = await ChatGptOAuthProtocol.ReadBoundedAsync(response.Content, 65536, cancellationToken);
                details = ChatGptOAuthProtocol.Failure(response, bytes).Details;
                using var body = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                classified = AgentProviderErrors.FromJson(body.RootElement, status, retryAfter);
            }
            catch (Exception error) when (error is JsonException or ChatGptAccountException or IOException or HttpRequestException) { }
            throw new AgentProviderException(classified.Code, classified.Retryable, retryAfter, classified.CanResume) { Details = details };
        }
    }
}
