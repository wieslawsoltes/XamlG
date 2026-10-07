using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace XamlG.Mcp;

/// <summary>Keeps modern subscriptions on their originating response stream, including
/// stateless HTTP. Legacy session notifications remain owned by the SDK.</summary>
internal sealed partial class AutomationMcpSubscriptions : IDisposable
{
    private const int MaximumSubscriptions = 64;
    private static readonly JsonSerializerOptions NotificationJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private int _active;

    public async ValueTask<EmptyResult> ListenAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _active) > MaximumSubscriptions)
        {
            Interlocked.Decrement(ref _active);
            throw new McpProtocolException("The catalog subscription limit has been reached. Close an existing subscription before retrying.", (McpErrorCode)(-32000));
        }
        try
        {
            var options = request.Server.ServerOptions;
            var requested = request.Params.Notifications;
            var tools = requested.ToolsListChanged == true ? options.ToolCollection : null;
            var resources = requested.ResourcesListChanged == true ? options.ResourceCollection : null;
            var prompts = requested.PromptsListChanged == true ? options.PromptCollection : null;
            var resourceUris = SupportedUris(requested.ResourceSubscriptions);
            var granted = new SubscriptionsListenNotifications
            {
                ToolsListChanged = tools != null ? true : null,
                ResourcesListChanged = resources != null ? true : null,
                PromptsListChanged = prompts != null ? true : null,
                ResourceSubscriptions = resourceUris.Length == 0 ? null : resourceUris
            };
            // A pending bit per catalog coalesces bursts without losing another catalog's
            // change. The wake-up channel and pending state have constant size even if a
            // client stops reading. Only this request handler writes to the transport.
            var changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
            { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.DropWrite });
            var pending = 0;
            var resourceGate = new object(); var pendingResources = new HashSet<string>(StringComparer.Ordinal);
            void Signal(int kind) { Interlocked.Or(ref pending, kind); changes.Writer.TryWrite(0); }
            void ToolsChanged(object? _, EventArgs __) => Signal(1);
            void ResourcesChanged(object? _, EventArgs __) => Signal(2);
            void PromptsChanged(object? _, EventArgs __) => Signal(4);
            void ResourceUpdated(string uri)
            {
                if (!resourceUris.Contains(uri, StringComparer.Ordinal)) return;
                lock (resourceGate) pendingResources.Add(uri);
                Signal(8);
            }
            if (tools != null) tools.Changed += ToolsChanged;
            if (resources != null) resources.Changed += ResourcesChanged;
            if (prompts != null) prompts.Changed += PromptsChanged;
            ResourceChanged += ResourceUpdated;
            try
            {
                // Install observers before acknowledging, then serialize every send so the
                // acknowledgement is always the first notification on this subscription.
                await SendAsync(NotificationMethods.SubscriptionsAcknowledgedNotification,
                    JsonSerializer.SerializeToNode(new SubscriptionsAcknowledgedNotificationParams { Notifications = granted }, NotificationJson)!.AsObject());
                if (tools == null && resources == null && prompts == null && resourceUris.Length == 0) return new();
                await foreach (var _ in changes.Reader.ReadAllAsync(cancellationToken))
                {
                    var kinds = Interlocked.Exchange(ref pending, 0);
                    if ((kinds & 1) != 0) await SendAsync(NotificationMethods.ToolListChangedNotification);
                    if ((kinds & 2) != 0) await SendAsync(NotificationMethods.ResourceListChangedNotification);
                    if ((kinds & 4) != 0) await SendAsync(NotificationMethods.PromptListChangedNotification);
                    if ((kinds & 8) != 0)
                    {
                        string[] updates;
                        lock (resourceGate) { updates = pendingResources.ToArray(); pendingResources.Clear(); }
                        foreach (var uri in updates) await SendAsync(NotificationMethods.ResourceUpdatedNotification, new JsonObject { ["uri"] = uri });
                    }
                }
                return new();
            }
            finally
            {
                if (tools != null) tools.Changed -= ToolsChanged;
                if (resources != null) resources.Changed -= ResourcesChanged;
                if (prompts != null) prompts.Changed -= PromptsChanged;
                ResourceChanged -= ResourceUpdated;
            }
        }
        finally { Interlocked.Decrement(ref _active); }

        Task SendAsync(string method, JsonObject? parameters = null)
        {
            parameters ??= new();
            parameters["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/subscriptionId"] = request.JsonRpcRequest.Id.Id switch
                {
                    string id => JsonValue.Create(id),
                    long id => JsonValue.Create(id),
                    _ => null
                }
            };
            // request.Server is the SDK's destination-bound server. It routes this message
            // to the held-open HTTP POST, without broadcasting to another client or request.
            return request.Server.SendMessageAsync(new JsonRpcNotification { Method = method, Params = parameters }, cancellationToken);
        }
    }
}
