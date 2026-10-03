using System.Collections.Concurrent;
using System.Text.Json;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

/// <summary>Concurrent JSON-RPC with independently versioned document and project snapshots.</summary>
public sealed class XamlLanguageServer : IAsyncDisposable
{
    private readonly LspConnection _connection;
    private readonly LspDocumentStore _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LspCompilationSnapshot> _requestProjects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _diagnostics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly SemaphoreSlim _parallelism = new(8);
    private readonly CancellationTokenSource _lifetime = new();
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
        _connection = new(input, output, lifetimeToken: _lifetime.Token);
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
            before = Volatile.Read(ref _project);
            after = new(checked(before.Revision + 1), compiler);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _project, after, before), before));
        foreach (var request in _requestProjects)
            if (!ReferenceEquals(request.Value, after) && _requests.TryGetValue(request.Key, out var cancellation)) Cancel(cancellation);
        foreach (var document in _documents.Snapshots) ScheduleDiagnostics(document);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, _connection.Closed);
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
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                        !root.TryGetProperty("method", out var methodNode) || methodNode.ValueKind != JsonValueKind.String)
                    { await ErrorAsync(null, -32600, "Invalid JSON-RPC request."); continue; }
                    var method = methodNode.GetString()!;
                    var parameters = root.TryGetProperty("params", out var supplied) ? supplied.Clone() : default;
                    if (root.TryGetProperty("id", out var id))
                    {
                        if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) { await ErrorAsync(null, -32600, "Request IDs must be strings or numbers."); continue; }
                        var key = id.GetRawText();
                        var source = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
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
            try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        }
        if (_connection.Failure is { } failure) throw failure;
    }

    private async Task ProcessRequestAsync(string key, JsonElement id, string method, JsonElement parameters, CancellationTokenSource source)
    {
        var acquired = false;
        LspCompilationSnapshot? snapshot = null;
        LspDocumentSnapshot? document = null;
        try
        {
            await _parallelism.WaitAsync(source.Token); acquired = true;
            object? result;
            if (method == LspMethods.Initialize)
            {
                if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
                    throw new LspRequestException(-32600, "The server is already initialized.");
                result = new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16", textDocumentSync = new { openClose = true, change = 2 },
                        hoverProvider = true,
                        completionProvider = new { resolveProvider = false, triggerCharacters = new[] { "<", ":", " ", "=", "{" } },
                        definitionProvider = true, referencesProvider = true, documentHighlightProvider = true,
                        documentSymbolProvider = true, foldingRangeProvider = true,
                        semanticTokensProvider = new { legend = new { tokenTypes = LspSemanticTokens.Legend, tokenModifiers = Array.Empty<string>() }, full = true }
                    },
                    serverInfo = new { name = "XamlG", version = "0.1.0-alpha.1" }
                };
            }
            else if (Volatile.Read(ref _initialized) == 0) throw new LspRequestException(-32002, "The server has not been initialized.");
            else if (method == LspMethods.Shutdown)
            {
                _shutdown = true;
                foreach (var diagnostic in _diagnostics.Values) Cancel(diagnostic);
                result = null;
            }
            else if (_shutdown) throw new LspRequestException(-32600, "The server is shutting down.");
            else
            {
                snapshot = Volatile.Read(ref _project);
                document = _documents.Get(LspConversions.DocumentUri(parameters));
                _requestProjects[key] = snapshot;
                result = new LspRequestHandler(snapshot.Compiler, _documents).Handle(method, parameters, source.Token);
            }
            bool IsCurrent() => snapshot == null || !_shutdown &&
                ReferenceEquals(snapshot, Volatile.Read(ref _project)) && document != null && _documents.IsCurrent(document);
            if (!await _connection.TryWriteAsync(new { jsonrpc = "2.0", id, result }, IsCurrent, source.Token))
                await ErrorAsync(id, -32801, "The document or project changed before this result could be published.");
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested || _connection.Closed.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested && !_connection.Closed.IsCancellationRequested)
                await ErrorAsync(id, snapshot != null && !ReferenceEquals(snapshot, Volatile.Read(ref _project)) ? -32801 : -32800,
                    "The request was cancelled or its project snapshot was superseded.");
        }
        catch (LspTransportException error) { await _log.WriteLineAsync("transport: " + error.Message); }
        catch (LspRequestException error) { await ErrorAsync(id, error.Code, error.Message); }
        catch (Exception error)
        {
            await _log.WriteLineAsync(error.ToString());
            await ErrorAsync(id, error is JsonException or KeyNotFoundException or InvalidOperationException ? -32602 : -32603, error.Message);
        }
        finally
        {
            _requestProjects.TryRemove(key, out _);
            if (acquired) _parallelism.Release();
            _requests.TryRemove(key, out _); source.Dispose();
        }
    }

    private async Task NotifyAsync(string method, JsonElement parameters)
    {
        if (method == LspMethods.Cancel)
        {
            if (parameters.TryGetProperty("id", out var id) && _requests.TryGetValue(id.GetRawText(), out var source)) Cancel(source);
            return;
        }
        if (Volatile.Read(ref _initialized) == 0 || _shutdown) return;
        LspDocumentSnapshot? document = null;
        if (method == LspMethods.Open)
        {
            var value = parameters.GetProperty("textDocument");
            document = _documents.Open(value.GetProperty("uri").GetString()!, value.GetProperty("version").GetInt32(), value.GetProperty("text").GetString()!);
        }
        else if (method == LspMethods.Change)
        {
            var changes = parameters.GetProperty("contentChanges").EnumerateArray().Select(change =>
                new LspTextChange(change.TryGetProperty("range", out var range) ? LspConversions.Range(range) : null, change.GetProperty("text").GetString()!)).ToArray();
            document = _documents.Change(LspConversions.DocumentUri(parameters), parameters.GetProperty("textDocument").GetProperty("version").GetInt32(), changes);
        }
        else if (method == LspMethods.Close)
        {
            var uri = LspConversions.DocumentUri(parameters);
            _documents.Close(uri);
            if (_diagnostics.TryRemove(uri, out var pending)) Cancel(pending);
            await _connection.WriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics, @params = new { uri, diagnostics = Array.Empty<object>() } }, _lifetime.Token);
        }
        if (document != null) ScheduleDiagnostics(document);
    }

    private void ScheduleDiagnostics(LspDocumentSnapshot document)
    {
        lock (_diagnosticGate)
        {
            if (_disposed || _shutdown || _stopping) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _connection.Closed);
            if (_diagnostics.TryGetValue(document.Uri, out var previous)) Cancel(previous);
            _diagnostics[document.Uri] = cancellation;
            Track(PublishDiagnosticsAsync(document, Volatile.Read(ref _project), cancellation));
        }
    }

    private async Task PublishDiagnosticsAsync(LspDocumentSnapshot document, LspCompilationSnapshot project, CancellationTokenSource cancellation)
    {
        bool IsCurrent() => !_shutdown && !_stopping && _documents.IsCurrent(document) &&
            ReferenceEquals(project, Volatile.Read(ref _project));
        try
        {
            await _parallelism.WaitAsync(cancellation.Token);
            XamlAnalysis analysis;
            try
            {
                if (!IsCurrent()) return;
                analysis = await Task.Run(() => project.Compiler.Analyze(document.Syntax, cancellation.Token), cancellation.Token);
            }
            finally { _parallelism.Release(); }
            var diagnostics = analysis.Output.Diagnostics.Select(d => new
            {
                range = LspConversions.Range(document.Syntax, d.Span),
                severity = d.Severity switch { XamlSeverity.Error => 1, XamlSeverity.Warning => 2, XamlSeverity.Info => 3, _ => 4 },
                code = d.Code, source = "XamlG", message = d.Message
            }).ToArray();
            await _connection.TryWriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics,
                @params = new { uri = document.Uri, version = document.Version, diagnostics } }, IsCurrent, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (LspTransportException error) { await _log.WriteLineAsync("transport: " + error.Message); }
        catch (Exception error) { await _log.WriteLineAsync("diagnostics: " + error.Message); }
        finally
        {
            ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_diagnostics).Remove(new(document.Uri, cancellation));
            cancellation.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource source) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
    private async ValueTask ErrorAsync(JsonElement? id, int code, string message)
    {
        if (_stopping || _connection.Closed.IsCancellationRequested) return;
        try { await _connection.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _connection.Closed.IsCancellationRequested) { }
        catch (LspTransportException error) { await _log.WriteLineAsync("transport: " + error.Message); }
    }
    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _taskId); _tasks[id] = task;
        _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_diagnosticGate)
        {
            if (_disposed) return;
            _disposed = true; _stopping = true;
        }
        _lifetime.Cancel();
        try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        await _connection.DisposeAsync(); _parallelism.Dispose(); _lifetime.Dispose();
    }
}
