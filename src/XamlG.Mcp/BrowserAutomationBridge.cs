using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Mcp;

/// <summary>
/// Pairs one authenticated browser with an automation host. Authentication and origin checks
/// belong to the embedding HTTP host; no request is dispatched before it accepts the catalog.
/// </summary>
public sealed class BrowserAutomationBridge : IAutomationHost
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly object _gate = new();
    private WebSocket? _socket;
    private long _nextId;
    private BrowserCatalog _catalog = new([], [], []);
    public bool IsConnected { get { lock (_gate) return _socket?.State == WebSocketState.Open; } }
    public IReadOnlyList<AutomationTool> Tools => _catalog.Tools;
    public IReadOnlyList<AutomationResource> Resources => _catalog.Resources;
    public IReadOnlyList<AutomationPrompt> Prompts => _catalog.Prompts;
    public event Action? CatalogChanged;

    public async Task RunAsync(WebSocket socket, BrowserCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog.Tools.Count is < 1 or > 1024 || catalog.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Tools.Count)
            throw new AutomationException("invalid_catalog", "Invalid or duplicate browser tool catalog.");
        lock (_gate)
        {
            if (_socket != null) throw new AutomationException("already_paired", "This companion is already paired with an IDE.");
            _socket = socket; _catalog = catalog;
        }
        try
        {
            await SendAsync(socket, new { kind = "ready" }, cancellationToken);
            CatalogChanged?.Invoke();
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message == null) break;
                var item = message.Value;
                if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id))
                    throw new AutomationException("invalid_message", "A response ID is required.");
                if (!_pending.TryGetValue(id, out var completion)) continue; // Late cancelled reply.
                if (item.TryGetProperty("error", out var error))
                    completion.TrySetException(new AutomationException("ide_error", error.GetProperty("message").GetString() ?? "IDE operation failed."));
                else if (item.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                else completion.TrySetException(new AutomationException("invalid_message", "Missing tool result."));
            }
        }
        finally
        {
            lock (_gate) { _socket = null; _catalog = new([], [], []); }
            foreach (var completion in _pending.Values)
                completion.TrySetException(new AutomationException("disconnected", "The IDE disconnected. Inspect live state before retrying an operation."));
            CatalogChanged?.Invoke();
        }
    }

    public async ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context)
    {
        var tool = Tools.SingleOrDefault(t => t.Name == name) ?? throw new AutomationException("unknown_tool", "Unknown tool or no paired IDE.");
        AutomationSchema.Validate(tool.InputSchema, arguments);
        return await RequestAsync("call", name, arguments, context.CancellationToken);
    }

    public async ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context)
    {
        if (!Resources.Any(r => r.Uri == uri)) throw new AutomationException("unknown_resource", "Unknown resource or no paired IDE.");
        return (await RequestAsync("resource", uri, AutomationJson.Element(new { }), context.CancellationToken)).GetString()!;
    }

    private async Task<JsonElement> RequestAsync(string method, string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        WebSocket socket;
        lock (_gate) socket = _socket ?? throw new AutomationException("disconnected", "Pair the browser IDE first.");
        if (_pending.Count >= 32) throw new AutomationException("busy", "The companion request limit is reached.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(id, completion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await SendAsync(socket, new { kind = "request", id, method, name, arguments }, deadline.Token);
            try { return await completion.Task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try
                {
                    using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(socket, new { kind = "cancel", id }, cancelDeadline.Token);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync<T>(WebSocket socket, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AutomationJson.Options);
        if (bytes.Length > AutomationSchema.MaximumArgumentBytes) throw new AutomationException("message_too_large", "The message exceeds 8 MiB.");
        await _sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    public static async Task<JsonElement?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new AutomationException("invalid_message", "Only JSON text messages are accepted.");
            if (stream.Length + result.Count > AutomationSchema.MaximumArgumentBytes) throw new AutomationException("message_too_large", "The message exceeds 8 MiB.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        using var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
    }
}

public sealed record BrowserCatalog(IReadOnlyList<AutomationTool> Tools,
    IReadOnlyList<AutomationResource> Resources, IReadOnlyList<AutomationPrompt> Prompts);
