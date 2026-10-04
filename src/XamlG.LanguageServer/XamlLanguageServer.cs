using System.Collections.Concurrent;
using System.Text.Json;
using XamlG.LanguageServer.Diagnostics;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

/// <summary>JSON-RPC publication is guarded by project and complete open-buffer revisions.
/// Graceful input termination drains admitted frames before cancelling the transport lifetime.</summary>
public sealed partial class XamlLanguageServer : IAsyncDisposable
{
    private readonly LspConnection _connection;
    private readonly LspSemanticTokenCache _semanticTokens = new();
    private readonly LspProjectAnalysisCache _analysisCache;
    private bool _versionedEdits;
    private readonly LspDocumentStore _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LspCompilationSnapshot> _requestProjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _requestDocumentSets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _diagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly SemaphoreSlim _parallelism = new(8);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _transportLifetime = new();
    private readonly TextWriter _log;
    private readonly object _diagnosticGate = new();
    private LspCompilationSnapshot _project;
    private long _taskId;
    private int _initialized;
    private volatile bool _shutdown;
    private volatile bool _stopping;
    private volatile bool _disposed;

    public XamlLanguageServer(XamlCompilationSession compiler, Stream input, Stream output, TextWriter? log = null)
    {
        _project = new(0, compiler);
        _analysisCache = new(_lifetime.Token);
        _connection = new(input, output, lifetimeToken: _transportLifetime.Token);
        _log = log ?? TextWriter.Null;
    }
    public long ProjectRevision => Volatile.Read(ref _project).Revision;
    public void UpdateCompilation(XamlCompilationSession compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        if (_disposed || _shutdown || _stopping) return;
        LspCompilationSnapshot before, after;
        do
        {
            before = Volatile.Read(ref _project); after = new(checked(before.Revision + 1), compiler);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _project, after, before), before));
        foreach (var request in _requestProjects)
            if (!ReferenceEquals(request.Value, after) && _requests.TryGetValue(request.Key, out var cancellation)) Cancel(cancellation);
        RefreshDocumentSet();
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, _connection.Closed);
        using var abort = cancellationToken.Register(static state => Cancel((CancellationTokenSource)state!), _transportLifetime);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                JsonDocument? message;
                try { message = await _connection.ReadAsync(linked.Token); }
                catch (JsonException error) { await ErrorAsync(null, -32700, error.Message); continue; }
                if (message == null) break;
                using (message)
                {
                    var root = message.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                    { await ErrorAsync(null, -32600, "Invalid JSON-RPC request."); continue; }
                    if (!root.TryGetProperty("method", out var methodNode))
                    {
                        if (!AcceptClientResponse(root)) await ErrorAsync(null, -32600, "Invalid JSON-RPC message.");
                        continue;
                    }
                    if (methodNode.ValueKind != JsonValueKind.String)
                    { await ErrorAsync(null, -32600, "Invalid JSON-RPC method."); continue; }
                    var method = methodNode.GetString()!;
                    var parameters = root.TryGetProperty("params", out var supplied) ? supplied.Clone() : default;
                    if (root.TryGetProperty("id", out var id))
                    {
                        if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) { await ErrorAsync(null, -32600, "Request IDs must be strings or numbers."); continue; }
                        var key = id.GetRawText(); var source = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                        if (_requests.Count >= 128 || !_requests.TryAdd(key, source))
                        { source.Dispose(); await ErrorAsync(id.Clone(), -32600, "Duplicate request ID or too many pending requests."); continue; }
                        var capturedId = id.Clone();
                        Track(Task.Run(() => ProcessRequestAsync(key, capturedId, method, parameters, source), CancellationToken.None));
                    }
                    else
                    {
                        if (method == LspMethods.Exit) break;
                        try { await NotifyAsync(method, parameters); }
                        catch (Exception error) when (error is LspRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
                        { await _log.WriteLineAsync("notification: " + error.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            lock (_diagnosticGate) _stopping = true;
            _lifetime.Cancel();
            foreach (var request in _requests.Values) Cancel(request);
            foreach (var diagnostic in _diagnostics.Values) Cancel(diagnostic);
            // Cancellation discards queued analysis, but the independently owned transport
            // lifetime allows admitted responses/refresh requests to finish their final flush.
            try
            {
                await Task.WhenAll(_tasks.Values);
                if (_diagnosticRefresh != null) await _diagnosticRefresh.DisposeAsync();
            }
            catch (OperationCanceledException) { }
            finally { Cancel(_transportLifetime); }
        }
        if (_connection.Failure is { } failure) throw failure;
    }

    private async Task ProcessRequestAsync(string key, JsonElement id, string method, JsonElement parameters, CancellationTokenSource source)
    {
        var acquired = false;
        LspCompilationSnapshot? project = null;
        LspDocumentSetSnapshot? buffers = null;
        try
        {
            await _parallelism.WaitAsync(source.Token); acquired = true;
            object? result;
            if (method == LspMethods.Initialize)
            {
                if (Interlocked.CompareExchange(ref _initialized, -1, 0) != 0) throw new LspRequestException(-32600, "The server is already initialized.");
                result = InitializeFeatures(parameters);
                Volatile.Write(ref _initialized, 1);
            }
            else if (Volatile.Read(ref _initialized) != 1) throw new LspRequestException(-32002, "The server has not been initialized.");
            else if (method == LspMethods.Shutdown)
            {
                _shutdown = true; foreach (var diagnostic in _diagnostics.Values) Cancel(diagnostic); result = null;
            }
            else if (_shutdown) throw new LspRequestException(-32600, "The server is shutting down.");
            else
            {
                project = Volatile.Read(ref _project); buffers = _documents.Capture();
                _requestProjects[key] = project; _requestDocumentSets[key] = buffers.Revision;
                var workspace = await _analysisCache.GetWorkspaceAsync(project.Compiler, buffers, source.Token);
                result = HandleWorkspaceRequest(method, parameters, workspace, buffers, source.Token);
            }
            bool IsCurrent() => project == null || !_shutdown && ReferenceEquals(project, Volatile.Read(ref _project)) && buffers != null && _documents.IsCurrent(buffers);
            if (!await _connection.TryWriteAsync(new { jsonrpc = "2.0", id, result }, IsCurrent, source.Token))
                await StaleResultAsync(id, method);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested || _connection.Closed.IsCancellationRequested ||
            project != null && !ReferenceEquals(project, Volatile.Read(ref _project)) || buffers != null && !_documents.IsCurrent(buffers))
        {
            if (!_lifetime.IsCancellationRequested && !_connection.Closed.IsCancellationRequested)
            {
                var superseded = project != null && !ReferenceEquals(project, Volatile.Read(ref _project)) || buffers != null && !_documents.IsCurrent(buffers);
                if (superseded) await StaleResultAsync(id, method);
                else await ErrorAsync(id, -32800, "The request was cancelled.");
            }
        }
        catch (LspTransportException error) { await _log.WriteLineAsync("transport: " + error.Message); }
        catch (LspRequestException error) { await ErrorAsync(id, error.Code, error.Message); }
        catch (Exception error)
        {
            await _log.WriteLineAsync(error.ToString());
            await ErrorAsync(id, error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException ? -32602 : -32603, error.Message);
        }
        finally
        {
            _requestProjects.TryRemove(key, out _); _requestDocumentSets.TryRemove(key, out _);
            if (acquired) _parallelism.Release();
            _requests.TryRemove(key, out _); source.Dispose();
        }
    }
    private ValueTask StaleResultAsync(JsonElement id, string method) => LspDiagnosticMethods.IsPull(method)
        ? ErrorAsync(id, -32802, "The diagnostic snapshot was superseded; pull again.", new { retriggerRequest = true })
        : ErrorAsync(id, -32801, "The project or open buffers changed before this result could be published.");

    private async Task NotifyAsync(string method, JsonElement parameters)
    {
        if (method == LspMethods.Cancel)
        {
            if (parameters.TryGetProperty("id", out var id) && _requests.TryGetValue(id.GetRawText(), out var source)) Cancel(source);
            return;
        }
        if (Volatile.Read(ref _initialized) != 1 || _shutdown) return;
        if (method == LspMethods.Open)
        {
            var value = parameters.GetProperty("textDocument");
            var uri = value.GetProperty("uri").GetString()!;
            var isCSharp = value.TryGetProperty("languageId", out var language) && language.GetString() == "csharp" ||
                System.Uri.TryCreate(uri, UriKind.Absolute, out var address) && address.AbsolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharp) _documents.OpenCSharp(uri, value.GetProperty("version").GetInt32(), value.GetProperty("text").GetString()!);
            else _documents.Open(uri, value.GetProperty("version").GetInt32(), value.GetProperty("text").GetString()!);
        }
        else if (method == LspMethods.Change)
        {
            var changes = parameters.GetProperty("contentChanges").EnumerateArray().Select(change =>
                new LspTextChange(change.TryGetProperty("range", out var range) ? LspConversions.Range(range) : null, change.GetProperty("text").GetString()!)).ToArray();
            var uri = LspConversions.DocumentUri(parameters);
            var version = parameters.GetProperty("textDocument").GetProperty("version").GetInt32();
            if (_documents.IsCSharp(uri)) _documents.ChangeCSharp(uri, version, changes);
            else _documents.Change(uri, version, changes);
        }
        else if (method == LspMethods.Close)
        {
            var uri = LspConversions.DocumentUri(parameters); _documents.Close(uri); _semanticTokens.Remove(uri);
            if (_diagnostics.TryRemove(uri, out var pending)) Cancel(pending);
            if (!_clientFeatures.PullDiagnostics)
                await _connection.WriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics, @params = new { uri, diagnostics = Array.Empty<object>() } }, _lifetime.Token);
        }
        else return;
        RefreshDocumentSet();
    }
    private void RefreshDocumentSet()
    {
        _analysisCache.Invalidate();
        var buffers = _documents.Capture();
        foreach (var request in _requestDocumentSets)
            if (request.Value != buffers.Revision && _requests.TryGetValue(request.Key, out var cancellation)) Cancel(cancellation);
        if (_clientFeatures.PullDiagnostics) { if (!_shutdown && !_stopping) _diagnosticRefresh?.Signal(); return; }
        foreach (var document in buffers.Documents) ScheduleDiagnostics(document, buffers);
    }
    private void ScheduleDiagnostics(LspDocumentSnapshot document, LspDocumentSetSnapshot buffers)
    {
        lock (_diagnosticGate)
        {
            if (_disposed || _shutdown || _stopping) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _connection.Closed);
            if (_diagnostics.TryGetValue(document.Uri, out var previous)) Cancel(previous);
            _diagnostics[document.Uri] = cancellation;
            Track(PublishDiagnosticsAsync(document, buffers, Volatile.Read(ref _project), cancellation));
        }
    }
    private async Task PublishDiagnosticsAsync(LspDocumentSnapshot document, LspDocumentSetSnapshot buffers, LspCompilationSnapshot project, CancellationTokenSource cancellation)
    {
        bool IsCurrent() => !_shutdown && !_stopping && _documents.IsCurrent(document) && _documents.IsCurrent(buffers) && ReferenceEquals(project, Volatile.Read(ref _project));
        try
        {
            await _parallelism.WaitAsync(cancellation.Token);
            XamlAnalysis analysis;
            try
            {
                if (!IsCurrent()) return;
                var batch = await _analysisCache.GetAsync(project.Compiler, buffers, cancellation.Token);
                analysis = batch.Single(a => ReferenceEquals(a.Syntax, document.Syntax));
            }
            finally { _parallelism.Release(); }
            var diagnostics = LspDiagnosticProjection.Create(analysis, cancellation.Token);
            await _connection.TryWriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics,
                @params = new { uri = document.Uri, version = document.Version, diagnostics } }, IsCurrent, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !IsCurrent()) { }
        catch (LspTransportException error) { await _log.WriteLineAsync("transport: " + error.Message); }
        catch (Exception error) { await _log.WriteLineAsync("diagnostics: " + error.Message); }
        finally
        {
            ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_diagnostics).Remove(new(document.Uri, cancellation));
            cancellation.Dispose();
        }
    }
    private static void Cancel(CancellationTokenSource source) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
    private async ValueTask ErrorAsync(JsonElement? id, int code, string message, object? data = null)
    {
        if (_stopping || _connection.Closed.IsCancellationRequested) return;
        object error = data == null ? new { code, message } : new { code, message, data };
        try { await _connection.WriteAsync(new { jsonrpc = "2.0", id, error }, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _connection.Closed.IsCancellationRequested) { }
        catch (LspTransportException failure) { await _log.WriteLineAsync("transport: " + failure.Message); }
    }
    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _taskId); _tasks[id] = task;
        _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_diagnosticGate) { if (_disposed) return; _disposed = true; _stopping = true; }
        _lifetime.Cancel(); Cancel(_transportLifetime);
        try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        if (_diagnosticRefresh != null) await _diagnosticRefresh.DisposeAsync();
        await _analysisCache.DisposeAsync();
        await _connection.DisposeAsync();
        _parallelism.Dispose(); _transportLifetime.Dispose(); _lifetime.Dispose();
    }
}
