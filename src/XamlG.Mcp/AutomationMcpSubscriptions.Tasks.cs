using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace XamlG.Mcp;

internal sealed partial class AutomationMcpSubscriptions
{
    private AutomationMcpTaskStore? _taskStore;
    private event Action<string>? TaskChanged;

    public void Register(AutomationMcpTaskStore store)
    {
        lock (_gate)
        {
            if (ReferenceEquals(store, _taskStore)) return;
            if (_taskStore != null) throw new InvalidOperationException("Only one automation task store can be registered per MCP host.");
            _taskStore = store; store.TaskChanged += PublishTask;
        }
    }

    private void PublishTask(string id)
    {
        if (TaskChanged is not { } observers) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(id); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private async Task<string[]> SupportedTasksAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        // The Tasks extension adds a field to the core subscription payload. Read it
        // from the original message because the core SDK deliberately owns only core fields.
        var requested = request.JsonRpcRequest.Params?["notifications"]?["taskIds"];
        if (requested == null || _taskStore == null) return [];
        if (requested is not JsonArray ids || ids.Count > 32)
            throw new McpProtocolException("At most 32 task IDs can be subscribed.", McpErrorCode.InvalidParams);
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ids)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrEmpty(id) || id.Length > 128)
                throw new McpProtocolException("Task IDs must be bounded strings.", McpErrorCode.InvalidParams);
            // The store resolves only tasks belonging to this authenticated principal
            // and the current workspace; clientInfo never participates in ownership.
            if (await _taskStore.GetTaskAsync(id, cancellationToken) != null) supported.Add(id);
        }
        return supported.ToArray();
    }

    private async Task<JsonObject?> TaskNotificationAsync(string id, CancellationToken cancellationToken)
    {
        if (_taskStore == null || await _taskStore.GetTaskAsync(id, cancellationToken) is not { } info) return null;
        var notification = new JsonObject
        {
            ["taskId"] = info.TaskId,
            ["status"] = info.Status.ToString().ToLowerInvariant(),
            ["createdAt"] = JsonValue.Create(info.CreatedAt),
            ["lastUpdatedAt"] = JsonValue.Create(info.LastUpdatedAt),
            ["ttlMs"] = info.TimeToLive == null ? null : JsonValue.Create((long)info.TimeToLive.Value.TotalMilliseconds),
            ["pollIntervalMs"] = info.PollIntervalMs
        };
        if (info.Result is { } result) notification["result"] = JsonSerializer.SerializeToNode(result);
        if (info.Error is { } error) notification["error"] = JsonSerializer.SerializeToNode(error);
        return notification;
    }
}
