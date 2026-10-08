using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using Anthropic.Services;

namespace XamlG.Playground;

/// <summary>
/// Browser transport for the pinned official SDK. Its raw client constructor
/// allocates a native decompression handler even when passed an HttpClient.
/// Keep the SDK's services, model serialization, paging and SSE parser, replacing
/// only that raw HTTP boundary. The harness owns deadlines, cancellation and retries.
/// </summary>
internal sealed class BrowserAnthropicClient : AnthropicClient
{
    private readonly ClientOptions _browserOptions;
    private readonly Lazy<IAnthropicClientWithRawResponse> _raw;
    public BrowserAnthropicClient(ClientOptions options) : base(options)
    { _browserOptions = options; _raw = new(() => new BrowserRawClient(_browserOptions)); }
    public override IAnthropicClientWithRawResponse WithRawResponse => _raw.Value;
    public override IAnthropicClient WithOptions(Func<ClientOptions, ClientOptions> modifier) => new BrowserAnthropicClient(modifier(_browserOptions));

    private sealed class BrowserRawClient(ClientOptions options) : IAnthropicClientWithRawResponse
    {
        private ClientOptions _options = options;
        public HttpClient HttpClient { get => _options.HttpClient; init => _options.HttpClient = value; }
        public IReadOnlyList<DelegatingHandler> Handlers { get => _options.Handlers; init => _options.Handlers = value; }
        public string BaseUrl { get => _options.BaseUrl; init => _options.BaseUrl = value; }
        public bool ResponseValidation { get => _options.ResponseValidation; init => _options.ResponseValidation = value; }
        public int? MaxRetries { get => _options.MaxRetries; init => _options.MaxRetries = value; }
        public TimeSpan? Timeout { get => _options.Timeout; init => _options.Timeout = value; }
        public string? ApiKey { get => _options.ApiKey; init => _options.ApiKey = value; }
        public string? AuthToken { get => _options.AuthToken; init => _options.AuthToken = value; }
        public string? WebhookKey { get => _options.WebhookKey; init => _options.WebhookKey = value; }
        public IAnthropicClientWithRawResponse WithOptions(Func<ClientOptions, ClientOptions> modifier) => new BrowserRawClient(modifier(_options));
        public IMessageServiceWithRawResponse Messages => new MessageServiceWithRawResponse(this);
        public IModelServiceWithRawResponse Models => new ModelServiceWithRawResponse(this);
        public IFileServiceWithRawResponse Files => throw new NotSupportedException();
        public ISkillServiceWithRawResponse Skills => throw new NotSupportedException();
        public IOrganizationServiceWithRawResponse Organization => throw new NotSupportedException();
        public IBetaServiceWithRawResponse Beta => throw new NotSupportedException();

        public async Task<HttpResponse> Execute<T>(HttpRequest<T> operation, CancellationToken cancellationToken = default) where T : ParamsBase
        {
            if (operation.Params is not (MessageCreateParams or ModelListParams))
                throw new NotSupportedException("The browser agent transport supports model discovery and Messages streaming.");
            using var request = new HttpRequestMessage(operation.Method, operation.Params.Url(_options));
            if (operation.Params is MessageCreateParams message)
            {
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(message.RawBodyData));
                request.Content.Headers.ContentType = new("application/json");
            }
            request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            foreach (var header in operation.Params.RawHeaderData)
                if (header.Key is not ("x-api-key" or "Authorization"))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToString());
            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            try { response.EnsureSuccessStatusCode(); return new() { RawMessage = response }; }
            catch { response.Dispose(); throw; }
        }
        // Views borrow the parent connection, which owns the HttpClient lifetime.
        public void Dispose() { }
    }
}
