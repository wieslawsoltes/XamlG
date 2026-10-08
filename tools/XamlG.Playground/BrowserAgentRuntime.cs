using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using OpenAI.Models;
using OpenAI.Responses;
using XamlG.Agents;
using XamlG.Agents.Anthropic;
using XamlG.Agents.Gemini;
using XamlG.Agents.OpenAI;
using XamlG.Automation;

namespace XamlG.Playground;

/// <summary>Page-owned agents using the official C# SDKs and the owner's live IDE adapter.</summary>
public sealed class BrowserAgentRuntime : IDisposable
{
    private readonly Dictionary<string, BrowserProvider> _providers;
    private CancellationTokenSource _workspace = new();
    private bool _disposed;
    public BrowserAgentRuntime(IAutomationHost host)
    {
        _providers = new[] { "openai", "anthropic", "gemini" }.ToDictionary(id => id, id => new BrowserProvider(id), StringComparer.Ordinal);
        Session = new(host, _providers.Values, new AutomationAgentWorkspace(host),
            new AgentPermissionConstraints(deniedTools: ["xamlg_layout_set", "xamlg_layout_reset"]));
    }
    public AgentWorkbenchSession Session { get; }
    public CancellationToken WorkspaceLifetime => _workspace.Token;
    public void Configure(string provider, string key, bool browserExposureAccepted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Session.IsRunning) throw new InvalidOperationException("Stop the active run before changing provider credentials.");
        if (!browserExposureAccepted) throw new InvalidOperationException("Accept browser key exposure before using Direct API mode.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a valid provider API key without whitespace.");
        if (!_providers.TryGetValue(provider, out var selected)) throw new ArgumentException("Choose a supported API provider.");
        ClearCredentials();
        selected.Configure(key);
    }
    public void ConfigureRelay(string provider, string address, string token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Session.IsRunning) throw new InvalidOperationException("Stop the active run before changing provider connections.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Use a loopback HTTP(S) relay origin without a path, query or credentials.");
        if (token.Length is < 32 or > 256 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("Enter the relay's local Owner token.");
        if (!_providers.TryGetValue(provider, out var selected)) throw new ArgumentException("Choose a supported API provider.");
        ClearCredentials(); selected.Configure("relay-transport", new(endpoint, token));
    }
    private sealed record RelayConnection(Uri Endpoint, string Token);
    public void ClearCredentials()
    {
        Session.Stop();
        foreach (var provider in _providers.Values) provider.Clear();
    }
    public void RetireWorkspace()
    {
        _workspace.Cancel(); ClearCredentials(); _workspace.Dispose(); _workspace = new();
    }
    public async Task<JsonElement> ExecuteAsync(string action, JsonElement arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _workspace.Token);
        if (action is "models" or "model_choices") request.CancelAfter(TimeSpan.FromMinutes(2));
        try { return await Session.ExecuteAsync(action, arguments, request.Token, _workspace.Token); }
        catch (Exception error) when (action is "models" or "model_choices" && error is not (OperationCanceledException or OutOfMemoryException))
        {
            // SDK error bodies and credentials never become UI messages.
            if (error is AgentProviderException failure) throw new InvalidOperationException("Model discovery failed: " + failure.Code + ". Check the selected provider and credentials.");
            throw new InvalidOperationException("Direct API model discovery failed. Check credentials and provider browser/CORS support, or select the local companion connection.");
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _workspace.Cancel(); ClearCredentials(); Session.Dispose();
        foreach (var provider in _providers.Values) provider.Dispose();
        _workspace.Dispose();
    }

    // Tasks retain a credential-free provider identity. Clearing a connection revokes
    // its lifetime and drops SDK clients; native conversation state can remain in memory.
    private sealed class BrowserProvider(string id) : IAgentProvider, IAgentProviderSession, IAgentProviderState, IDisposable
    {
        private SdkConnection? _shape;
        private SdkConnection? _connection;
        private CancellationTokenSource? _credentials;
        public string Id => id;
        public void Configure(string key, RelayConnection? relay = null) { Clear(); _connection = SdkConnection.Create(id, key, relay); _credentials = new(); }
        public void Clear()
        {
            _credentials?.Cancel(); _credentials?.Dispose(); _credentials = null;
            _connection?.Dispose(); _connection = null;
        }
        private IAgentProvider Connected => _connection?.Provider ?? throw new AgentProviderException("browser_credentials_required", false);
        private IAgentProvider Shape => (_shape ??= SdkConnection.Create(id, "context-shape-only")).Provider;
        public JsonElement SaveNative(object native) => ((IAgentProviderState)Shape).SaveNative(native);
        public object RestoreNative(JsonElement native) => ((IAgentProviderState)Shape).RestoreNative(native);
        public CancellationToken GetSessionLifetime() => _credentials?.Token ?? throw new AgentProviderException("browser_credentials_required", false);
        public int GetContextBytes(AgentRequest request) => Shape.GetContextBytes(request);
        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, GetSessionLifetime());
            var result = await Connected.ListModelsAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested(); return result;
        }
        public Task<AgentReply> GenerateAsync(AgentRequest request, Func<string, ValueTask> textDelta, CancellationToken cancellationToken) =>
            Connected.GenerateAsync(request, textDelta, cancellationToken);
        public void Dispose() { Clear(); _shape?.Dispose(); }
    }

    private sealed class SdkConnection(IAgentProvider provider, HttpClient http, object? sdk = null) : IDisposable
    {
        public IAgentProvider Provider { get; } = provider;
#pragma warning disable OPENAI001
        public static SdkConnection Create(string provider, string key, RelayConnection? relay = null)
        {
            var http = new HttpClient(new AgentHttpHandler(new BrowserProviderHandler(provider, relay)))
                { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 8 * 1024 * 1024 };
            try
            {
                switch (provider)
                {
                    case "openai":
                        var responses = new ResponsesClientOptions { RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(http) };
                        var models = new OpenAI.OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(0), Transport = new HttpClientPipelineTransport(http) };
                        return new(new OpenAIAgentProvider(new ResponsesClient(new System.ClientModel.ApiKeyCredential(key), responses), new OpenAIModelClient(new System.ClientModel.ApiKeyCredential(key), models)), http);
                    case "anthropic":
                        // ClientOptions' parameterless constructor creates a native decompression
                        // handler before object initializers run. Initialize the public value
                        // type explicitly so the official SDK uses the browser transport.
                        Anthropic.Core.ClientOptions anthropicOptions = default;
                        anthropicOptions.HttpClient = http; anthropicOptions.Handlers = [];
                        anthropicOptions.BaseUrl = "https://api.anthropic.com";
                        anthropicOptions.ApiKey = key; anthropicOptions.AuthToken = null; anthropicOptions.WebhookKey = null;
                        anthropicOptions.MaxRetries = 0;
                        var anthropic = new BrowserAnthropicClient(anthropicOptions);
                        return new(new AnthropicAgentProvider(anthropic), http, anthropic);
                    case "gemini":
                        var gemini = new Google.GenAI.Client(enterprise: false, apiKey: key,
                            httpOptions: new() { RetryOptions = new() { Attempts = 1 } }, clientOptions: new() { HttpClientFactory = () => http });
                        return new(new GeminiAgentProvider(gemini), http, gemini);
                    default: throw new ArgumentException("Unknown browser API provider.");
                }
            }
            catch { http.Dispose(); throw; }
        }
#pragma warning restore OPENAI001
        public void Dispose() { if (sdk is IDisposable disposable) disposable.Dispose(); http.Dispose(); }
    }

    private sealed class BrowserProviderHandler(string provider, RelayConnection? relay) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = provider switch { "openai" => "api.openai.com", "anthropic" => "api.anthropic.com", "gemini" => "generativelanguage.googleapis.com", _ => "" };
            if (request.RequestUri is not { Scheme: "https", Port: 443 } uri || uri.Host != host || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("Direct API requests must use the selected provider's official HTTPS endpoint.");
            if (relay != null)
            {
                var target = Uri.EscapeDataString(uri.PathAndQuery);
                request.RequestUri = new Uri(relay.Endpoint, "provider/" + provider + "?target=" + target);
                request.Headers.Clear(); request.Headers.Authorization = new("Bearer", relay.Token);
            }
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Omit);
            request.SetBrowserRequestMode(BrowserRequestMode.Cors);
            request.SetBrowserRequestOption("redirect", "error");
            request.SetBrowserRequestOption("cache", "no-store");
            request.SetBrowserResponseStreamingEnabled(true);
            if (relay == null && provider == "anthropic") request.Headers.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
