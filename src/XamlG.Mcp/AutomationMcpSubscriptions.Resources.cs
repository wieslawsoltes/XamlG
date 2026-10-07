using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XamlG.Automation;

namespace XamlG.Mcp;

internal sealed partial class AutomationMcpSubscriptions
{
    private readonly object _gate = new();
    private readonly HashSet<IAutomationHost> _hosts = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<McpServerOptions, Dictionary<string, LegacyResources>> _legacy = new();
    private event Action<string>? ResourceChanged;

    public void Register(IAutomationHost host)
    {
        lock (_gate)
            if (_hosts.Add(host) && host is IAutomationResourceEvents events) events.ResourceChanged += PublishResource;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var host in _hosts.OfType<IAutomationResourceEvents>()) host.ResourceChanged -= PublishResource;
            if (_taskStore != null) _taskStore.TaskChanged -= PublishTask;
            foreach (var entry in _legacy) foreach (var subscription in entry.Value.Values) subscription.Close();
            _hosts.Clear(); _legacy.Clear(); ResourceChanged = null; TaskChanged = null; _taskStore = null;
        }
    }
    private IAutomationHost[] Hosts { get { lock (_gate) return _hosts.ToArray(); } }
    private bool Supports(string uri) => Hosts.Any(host => host is IAutomationResourceEvents && host.Resources.Any(resource => AutomationUriTemplate.IsMatch(resource, uri)));
    private string[] SupportedUris(IList<string>? uris)
    {
        if (uris == null) return [];
        if (uris.Count > 128 || uris.Any(uri => string.IsNullOrEmpty(uri) || uri.Length > 4096))
            throw new McpProtocolException("At most 128 bounded resource URIs can be subscribed.", McpErrorCode.InvalidParams);
        return uris.Distinct(StringComparer.Ordinal).Where(Supports).ToArray();
    }
    private void PublishResource(string uri)
    {
        if (ResourceChanged is { } observers)
            foreach (Action<string> observer in observers.GetInvocationList())
                try { observer(uri); } catch (Exception error) when (error is not OutOfMemoryException) { }
        LegacyResources[] legacy;
        lock (_gate) legacy = _legacy.SelectMany(entry => entry.Value.Values).ToArray();
        foreach (var subscription in legacy) subscription.Notify(uri);
    }

    public ValueTask<EmptyResult> SubscribeAsync(RequestContext<SubscribeRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Params.Uri is not { Length: > 0 and <= 4096 } uri || !Supports(uri))
            throw new McpProtocolException("Unknown resource URI.", McpErrorCode.InvalidParams);
        lock (_gate)
        {
            var sessions = _legacy.GetValue(request.Server.ServerOptions, _ => new(StringComparer.Ordinal));
            var key = request.Server.SessionId ?? "stdio";
            if (!sessions.TryGetValue(key, out var subscription) || subscription.Closed)
            {
                foreach (var entry in _legacy)
                    foreach (var closed in entry.Value.Where(pair => pair.Value.Closed).Select(pair => pair.Key).ToArray()) entry.Value.Remove(closed);
                if (_legacy.Sum(entry => entry.Value.Count) >= MaximumSubscriptions)
                    throw new McpProtocolException("The legacy resource subscription limit has been reached.", (McpErrorCode)(-32000));
                sessions[key] = subscription = new(request.Server);
            }
            subscription.Add(uri);
        }
        return ValueTask.FromResult(new EmptyResult());
    }
    public ValueTask<EmptyResult> UnsubscribeAsync(RequestContext<UnsubscribeRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_legacy.TryGetValue(request.Server.ServerOptions, out var sessions) && sessions.TryGetValue(request.Server.SessionId ?? "stdio", out var state))
                state.Remove(request.Params.Uri);
        return ValueTask.FromResult(new EmptyResult());
    }
    public async ValueTask<CompleteResult> CompleteAsync(RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        if (request.Params.Ref is not ResourceTemplateReference { Uri: { } template }) return new();
        var host = Hosts.FirstOrDefault(host => host.Resources.Any(resource => resource.IsTemplate && resource.Uri == template));
        if (host is not IAutomationCompletions completions) return new();
        var result = await completions.CompleteAsync(template, request.Params.Argument.Name, request.Params.Argument.Value, new("mcp", cancellationToken));
        return new() { Completion = new() { Values = result.Values.Take(100).ToArray(), Total = result.Total, HasMore = result.HasMore || result.Values.Count > 100 } };
    }

    private sealed class LegacyResources(McpServer server)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _uris = new(StringComparer.Ordinal), _pending = new(StringComparer.Ordinal);
        private bool _sending;
        public bool Closed { get; private set; }
        public void Add(string uri)
        {
            lock (_gate)
            {
                if (_uris.Count >= 128 && !_uris.Contains(uri)) throw new McpProtocolException("At most 128 resources can be subscribed per session.", McpErrorCode.InvalidParams);
                _uris.Add(uri);
            }
        }
        public void Remove(string uri) { lock (_gate) { _uris.Remove(uri); _pending.Remove(uri); if (_uris.Count == 0) Closed = true; } }
        public void Close() { lock (_gate) { Closed = true; _uris.Clear(); _pending.Clear(); } }
        public void Notify(string uri)
        {
            lock (_gate)
            {
                if (Closed || !_uris.Contains(uri)) return;
                _pending.Add(uri); if (_sending) return; _sending = true;
            }
            _ = DrainAsync();
        }
        private async Task DrainAsync()
        {
            try
            {
                for (;;)
                {
                    string uri;
                    lock (_gate)
                    {
                        if (Closed || _pending.Count == 0) { _sending = false; return; }
                        uri = _pending.First(); _pending.Remove(uri);
                    }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await server.SendMessageAsync(new JsonRpcNotification { Method = NotificationMethods.ResourceUpdatedNotification, Params = new JsonObject { ["uri"] = uri } }, deadline.Token);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Close(); }
        }
    }
}
