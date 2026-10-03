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
    private LspCompilationSnapshot _project;
    private long _taskId;
    private volatile bool _initialized;
    private volatile bool _shutdown;
    private volatile bool _disposed;

    public XamlLanguageServer(XamlCompilationSession compiler, Stream input, Stream output, TextWriter? log = null)
    {
        _project = new(0, compiler);
        _connection = new(input, output);
        _log = log ?? TextWriter.Null;
    }
    public long ProjectRevision => Volatile.Read(ref _project).Revision;

    public void UpdateCompilation(XamlCompilationSession compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        if (_disposed || _shutdown || _lifetime.IsCancellationRequested) return;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
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
            _lifetime.Cancel();
            foreach (var request in _requests.Values) Cancel(request);
            foreach (var diagnostic in _diagnostics.Values) Cancel(diagnostic);
            try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessRequestAsync(string key, JsonElement id, string method, JsonElement parameters, CancellationTokenSource source)
    {
        var acquired = false;
        LspCompilationSnapshot? snapshot = null;
        try
        {
            await _parallelism.WaitAsync(source.Token); acquired = true;
            object? result;
            if (method == LspMethods.Initialize)
            {
                if (_initialized) throw new LspRequestException(-32600, "The server is already initialized.");
                _initialized = true;
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
            else if (!_initialized) throw new LspRequestException(-32002, "The server has not been initialized.");
            else if (method == LspMethods.Shutdown) { _shutdown = true; result = null; }
            else if (_shutdown) throw new LspRequestException(-32600, "The server is shutting down.");
            else
            {
                snapshot = Volatile.Read(ref _project);
                _requestProjects[key] = snapshot;
                result = new LspRequestHandler(snapshot.Compiler, _documents).Handle(method, parameters, source.Token);
                if (!ReferenceEquals(snapshot, Volatile.Read(ref _project))) throw new LspRequestException(-32801, "The project changed while this request was being processed.");
                source.Token.ThrowIfCancellationRequested();
            }
            await _connection.WriteAsync(new { jsonrpc = "2.0", id, result }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested)
                await ErrorAsync(id, snapshot != null && !ReferenceEquals(snapshot, Volatile.Read(ref _project)) ? -32801 : -32800, "The request was cancelled or its project snapshot was superseded.");
        }
        catch (LspRequestException error) { if (!_lifetime.IsCancellationRequested) await ErrorAsync(id, error.Code, error.Message); }
        catch (Exception error)
        {
            await _log.WriteLineAsync(error.ToString());
            if (!_lifetime.IsCancellationRequested) await ErrorAsync(id, error is JsonException or KeyNotFoundException or InvalidOperationException ? -32602 : -32603, error.Message);
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
        if (!_initialized || _shutdown) return;
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
        if (_disposed || _shutdown || _lifetime.IsCancellationRequested) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnostics.AddOrUpdate(document.Uri, cancellation, (_, old) => { Cancel(old); return cancellation; });
        Track(PublishDiagnosticsAsync(document, Volatile.Read(ref _project), cancellation));
    }

    private async Task PublishDiagnosticsAsync(LspDocumentSnapshot document, LspCompilationSnapshot project, CancellationTokenSource cancellation)
    {
        try
        {
            await _parallelism.WaitAsync(cancellation.Token);
            XamlAnalysis analysis;
            try
            {
                if (!_documents.IsCurrent(document) || !ReferenceEquals(project, Volatile.Read(ref _project))) return;
                analysis = await Task.Run(() => project.Compiler.Analyze(document.Syntax, cancellation.Token), cancellation.Token);
            }
            finally { _parallelism.Release(); }
            if (!_documents.IsCurrent(document) || !ReferenceEquals(project, Volatile.Read(ref _project))) return;
            var diagnostics = analysis.Output.Diagnostics.Select(d => new
            {
                range = LspConversions.Range(document.Syntax, d.Span),
                severity = d.Severity switch { XamlSeverity.Error => 1, XamlSeverity.Warning => 2, XamlSeverity.Info => 3, _ => 4 },
                code = d.Code, source = "XamlG", message = d.Message
            }).ToArray();
            await _connection.WriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics, @params = new { uri = document.Uri, version = document.Version, diagnostics } }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { await _log.WriteLineAsync("diagnostics: " + error.Message); }
        finally
        {
            ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_diagnostics).Remove(new(document.Uri, cancellation));
            cancellation.Dispose();
        }
    }

    private static void Cancel(CancellationTokenSource source) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
    private ValueTask ErrorAsync(JsonElement? id, int code, string message) => _connection.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, _lifetime.Token);
    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _taskId); _tasks[id] = task;
        _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        _lifetime.Cancel();
        try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        await _connection.DisposeAsync(); _parallelism.Dispose(); _lifetime.Dispose();
    }
}
