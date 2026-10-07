using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Mcp;

/// <summary>
/// Pairs one authenticated browser with an automation host. Authentication and origin checks
/// belong to the embedding HTTP host; no request is dispatched before it accepts the catalog.
/// </summary>
public sealed class BrowserAutomationBridge : IAutomationHost, IAutomationCatalogEvents
{
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly object _gate = new();
    private Session? _session;
    private long _nextId;
    public bool IsConnected { get { lock (_gate) return _session is { Closing: false } session && session.Socket.State == WebSocketState.Open; } }
    public IReadOnlyList<AutomationTool> Tools { get { lock (_gate) return _session?.Catalog.Tools ?? []; } }
    public IReadOnlyList<AutomationResource> Resources { get { lock (_gate) return _session?.Catalog.Resources ?? []; } }
    public IReadOnlyList<AutomationPrompt> Prompts { get { lock (_gate) return _session?.Catalog.Prompts ?? []; } }
    public event Action? CatalogChanged;

    /// <summary>Checks the private lease delivered only to the paired browser. Owner HTTP
    /// requests must also authenticate separately; the lease alone is not a credential.</summary>
    public bool TryGetOwnerSession(string lease, out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken = default;
            if (_session is not { Closing: false } session || session.Socket.State != WebSocketState.Open ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(lease)), session.LeaseHash)) return false;
            cancellationToken = session.Lifetime.Token;
            return true;
        }
    }

    public async Task RunAsync(WebSocket socket, BrowserCatalog catalog, CancellationToken cancellationToken)
    {
        if (catalog.Tools == null || catalog.Resources == null || catalog.Prompts == null ||
            catalog.Tools.Count is < 1 or > 1024 || catalog.Resources.Count > 1024 || catalog.Prompts.Count > 128 ||
            catalog.Tools.Any(t => t == null || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 64 ||
                !Enum.IsDefined(t.Scope) || !Enum.IsDefined(t.Effect) || t.InputSchema.ValueKind != JsonValueKind.Object) ||
            catalog.Resources.Any(r => r == null || string.IsNullOrWhiteSpace(r.Uri)) ||
            catalog.Prompts.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name)) ||
            catalog.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Tools.Count ||
            catalog.Resources.Select(r => r.Uri).Distinct(StringComparer.Ordinal).Count() != catalog.Resources.Count ||
            catalog.Prompts.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Prompts.Count)
            throw new AutomationException("invalid_catalog", "Invalid or duplicate browser catalog.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new Session(socket, new(Array.AsReadOnly(catalog.Tools.ToArray()),
            Array.AsReadOnly(catalog.Resources.ToArray()), Array.AsReadOnly(catalog.Prompts.ToArray())), lifetime);
        lock (_gate)
        {
            if (_session != null) throw new AutomationException("already_paired", "This companion is already paired with an IDE.");
            _session = session;
        }
        try
        {
            await SendAsync(socket, new { kind = "ready", ownerSession = session.Lease }, cancellationToken);
            NotifyCatalogChanged();
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message == null) break;
                var item = message.Value;
                if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id))
                    throw new AutomationException("invalid_message", "A response ID is required.");
                TaskCompletionSource<JsonElement>? completion;
                lock (_gate) session.Pending.TryGetValue(id, out completion);
                if (completion == null) continue; // Late cancelled reply.
                if (item.TryGetProperty("error", out var error))
                    completion.TrySetException(new AutomationException("ide_error", error.GetProperty("message").GetString() ?? "IDE operation failed."));
                else if (item.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                else completion.TrySetException(new AutomationException("invalid_message", "Missing tool result."));
            }
        }
        finally
        {
            lock (_gate)
            {
                // Detach and retire exactly this session before a new browser can pair.
                session.Closing = true;
                foreach (var completion in session.Pending.Values)
                    completion.TrySetException(Disconnected());
                session.Pending.Clear();
            }
            try { lifetime.Cancel(); }
            finally
            {
                lock (_gate) _session = null;
                NotifyCatalogChanged();
            }
        }
    }

    public async ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context)
    {
        Session session;
        lock (_gate) session = _session ?? throw Disconnected();
        var tool = session.Catalog.Tools.SingleOrDefault(t => t.Name == name) ?? throw new AutomationException("unknown_tool", "Unknown tool or no paired IDE.");
        AutomationSchema.Validate(tool.InputSchema, arguments);
        return await RequestAsync(session, "call", name, arguments, context);
    }

    public async ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context)
    {
        Session session;
        lock (_gate) session = _session ?? throw Disconnected();
        if (!session.Catalog.Resources.Any(r => r.Uri == uri)) throw new AutomationException("unknown_resource", "Unknown resource or no paired IDE.");
        return (await RequestAsync(session, "resource", uri, AutomationJson.Element(new { }), context)).GetString()!;
    }

    private async Task<JsonElement> RequestAsync(Session session, string method, string name, JsonElement arguments, AutomationCallContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Caller.Length > 200) throw new ArgumentException("Caller label exceeds 200 characters.");
        var socket = session.Socket;
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource deadline;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session) || session.Closing || socket.State != WebSocketState.Open) throw Disconnected();
            if (session.Pending.Count >= 32) throw new AutomationException("busy", "The companion request limit is reached.");
            deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, session.Lifetime.Token);
            session.Pending.Add(id, completion);
        }
        using var deadlineLifetime = deadline;
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await SendAsync(socket, new { kind = "request", id, method, name, arguments, caller = context.Caller }, deadline.Token);
            try { return await completion.Task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (session.Lifetime.IsCancellationRequested) throw Disconnected();
                try
                {
                    using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendAsync(socket, new { kind = "cancel", id }, cancelDeadline.Token);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { throw Disconnected(); }
        finally { lock (_gate) session.Pending.Remove(id); }
    }

    private static AutomationException Disconnected() => new("disconnected", "The IDE disconnected. Inspect live state before retrying an operation.");
    private void NotifyCatalogChanged()
    {
        if (CatalogChanged is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private sealed class Session(WebSocket socket, BrowserCatalog catalog, CancellationTokenSource lifetime)
    {
        public WebSocket Socket { get; } = socket;
        public BrowserCatalog Catalog { get; } = catalog;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public bool Closing { get; set; }
        public string Lease { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public byte[] LeaseHash => field ??= SHA256.HashData(Encoding.UTF8.GetBytes(Lease));
        public Dictionary<long, TaskCompletionSource<JsonElement>> Pending { get; } = [];
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
