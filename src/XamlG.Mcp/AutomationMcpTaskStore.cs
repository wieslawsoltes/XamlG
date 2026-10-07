using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace XamlG.Mcp;

/// <summary>Principal/workspace-bound, bounded task storage for noninteractive waits.
/// Task and artifact ownership never derives from clientInfo supplied by a client.</summary>
public sealed class AutomationMcpTaskStore : IMcpTaskStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tasks = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Scope?> _scope = new();
    private readonly Timer _expiry;
    private bool _disposed;
    public AutomationMcpTaskStore() => _expiry = new(_ => Expire(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    public event Action<string>? TaskChanged;
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived { add { } remove { } }
    public IReadOnlyList<McpTaskInfo> LocalInventory { get { lock (_gate) { ExpireCore(); return _tasks.Values.Select(entry => entry.Info with { Result = null, Error = null }).ToArray(); } } }
    internal CancellationToken WorkspaceLifetime => _scope.Value?.Workspace ?? default;
    internal CancellationToken TaskLifetime => _scope.Value?.Task ?? default;
    internal IDisposable Enter(string principal, CancellationToken workspace)
    {
        var previous = _scope.Value; _scope.Value = new(principal, workspace, default);
        return new ScopeRestore(() => _scope.Value = previous);
    }
    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = _scope.Value ?? throw new InvalidOperationException("Tasks require an authenticated request scope.");
        scope.Workspace.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ExpireCore();
            if (_tasks.Count >= 32) throw new McpProtocolException("At most 32 tasks may be retained. Clear completed tasks in the IDE.", (McpErrorCode)(-32000));
            var now = DateTimeOffset.UtcNow;
            var info = new McpTaskInfo(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), McpTaskStatus.Working, now, now, TimeSpan.FromSeconds(120), 1000);
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(scope.Workspace); lifetime.CancelAfter(TimeSpan.FromSeconds(120));
            _tasks.Add(info.TaskId, new(info, scope.Principal, scope.Workspace, lifetime));
            _scope.Value = scope with { Task = lifetime.Token };
            return Task.FromResult(info);
        }
    }
    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { ExpireCore(); return Task.FromResult(Owned(taskId)?.Info); }
    }
    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default) => Finish(taskId, result, false, cancellationToken);
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default) => Finish(taskId, error, true, cancellationToken);
    private Task Finish(string id, JsonElement value, bool failed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 65536) throw new InvalidOperationException("Wait results are limited to 64 KiB.");
        lock (_gate)
        {
            ExpireCore();
            if (Owned(id) is not { } entry || entry.Info.Status != McpTaskStatus.Working) return Task.CompletedTask;
            entry.Info = entry.Info with { Status = failed ? McpTaskStatus.Failed : McpTaskStatus.Completed, LastUpdatedAt = DateTimeOffset.UtcNow,
                Result = failed ? null : value.Clone(), Error = failed ? value.Clone() : null };
        }
        Notify(id); return Task.CompletedTask;
    }
    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry? entry;
        lock (_gate)
        {
            ExpireCore(); entry = Owned(taskId);
            if (entry == null || entry.Info.Status != McpTaskStatus.Working) return Task.FromResult(false);
            entry.Info = entry.Info with { Status = McpTaskStatus.Cancelled, LastUpdatedAt = DateTimeOffset.UtcNow };
        }
        CancelLifetime(entry.Lifetime); Notify(taskId); return Task.FromResult(true);
    }
    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Wait tasks never elicit information or authorize operations."));
    public void CancelLocal(string id)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out entry) || entry.Info.Status != McpTaskStatus.Working) return;
            entry.Info = entry.Info with { Status = McpTaskStatus.Cancelled, LastUpdatedAt = DateTimeOffset.UtcNow };
        }
        CancelLifetime(entry.Lifetime); Notify(id);
    }
    public void ClearFinishedLocal()
    {
        lock (_gate)
            foreach (var id in _tasks.Where(pair => pair.Value.Info.Status != McpTaskStatus.Working).Select(pair => pair.Key).ToArray()) Remove(id);
    }
    private Entry? Owned(string id) => _scope.Value is { } scope && _tasks.TryGetValue(id, out var entry) && entry.Principal == scope.Principal &&
        entry.Workspace == scope.Workspace && !entry.Workspace.IsCancellationRequested ? entry : null;
    private void Expire() { lock (_gate) if (!_disposed) ExpireCore(); }
    private void ExpireCore()
    {
        foreach (var id in _tasks.Where(pair => pair.Value.Workspace.IsCancellationRequested || pair.Value.Info.CreatedAt.AddSeconds(120) <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) Remove(id);
    }
    private void Remove(string id)
    {
        if (!_tasks.Remove(id, out var entry)) return;
        CancelLifetime(entry.Lifetime); entry.Lifetime.Dispose(); Notify(id);
    }
    private static void CancelLifetime(CancellationTokenSource lifetime)
    {
        try { lifetime.Cancel(); }
        // Expiry can dispose a source after an owner cancellation leaves the lock.
        // Callback exceptions must not prevent other expired entries being retired.
        catch (Exception error) when (error is ObjectDisposedException or AggregateException) { }
    }
    private void Notify(string id)
    {
        if (TaskChanged is not { } observers) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(id); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _expiry.Dispose(); foreach (var id in _tasks.Keys.ToArray()) Remove(id); TaskChanged = null; }
    }
    private sealed record Scope(string Principal, CancellationToken Workspace, CancellationToken Task);
    private sealed class ScopeRestore(Action restore) : IDisposable { public void Dispose() => restore(); }
    private sealed class Entry(McpTaskInfo info, string principal, CancellationToken workspace, CancellationTokenSource lifetime)
    { public McpTaskInfo Info = info; public string Principal = principal; public CancellationToken Workspace = workspace; public CancellationTokenSource Lifetime = lifetime; }
}
