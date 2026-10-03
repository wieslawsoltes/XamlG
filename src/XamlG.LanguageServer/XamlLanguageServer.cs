using System.Collections.Concurrent;
using System.Text.Json;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

/// <summary>Concurrent JSON-RPC request handling with cancellation, version-gated diagnostics and no transport dependency in the compiler.</summary>
public sealed class XamlLanguageServer : IAsyncDisposable
{
    private readonly LspConnection _connection;
    private readonly LspDocumentStore _documents = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _tasks = new();
    private readonly SemaphoreSlim _parallelism = new(8);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextWriter _log;
    private XamlCompilationSession _compiler;
    private long _taskId;
    private bool _initialized;
    private bool _shutdown;

    public XamlLanguageServer(XamlCompilationSession compiler, Stream input, Stream output, TextWriter? log = null)
    {
        _compiler = compiler;
        _connection = new(input, output);
        _log = log ?? TextWriter.Null;
    }

    public void UpdateCompilation(XamlCompilationSession compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        Interlocked.Exchange(ref _compiler, compiler);
        foreach (var document in _documents.Snapshots) Track(PublishDiagnosticsAsync(document));
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
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
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
            foreach (var request in _requests.Values) request.Cancel();
            try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessRequestAsync(string key, JsonElement id, string method, JsonElement parameters, CancellationTokenSource source)
    {
        var acquired = false;
        try
        {
            await _parallelism.WaitAsync(source.Token);
            acquired = true;
            object? result;
            if (method == LspMethods.Initialize)
            {
                _initialized = true;
                result = new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",
                        textDocumentSync = new { openClose = true, change = 2 },
                        hoverProvider = true,
                        completionProvider = new { resolveProvider = false, triggerCharacters = new[] { "<", ":", " ", "=", "{" } },
                        definitionProvider = true,
                        referencesProvider = true,
                        documentHighlightProvider = true,
                        documentSymbolProvider = true,
                        foldingRangeProvider = true,
                        semanticTokensProvider = new { legend = new { tokenTypes = LspSemanticTokens.Legend, tokenModifiers = Array.Empty<string>() }, full = true }
                    },
                    serverInfo = new { name = "XamlG", version = "0.1.0-alpha.1" }
                };
            }
            else if (!_initialized) throw new LspRequestException(-32002, "The server has not been initialized.");
            else if (method == LspMethods.Shutdown) { _shutdown = true; result = null; }
            else if (_shutdown) throw new LspRequestException(-32600, "The server is shutting down.");
            else result = new LspRequestHandler(Volatile.Read(ref _compiler), _documents).Handle(method, parameters, source.Token);
            await _connection.WriteAsync(new { jsonrpc = "2.0", id, result }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        { if (!_lifetime.IsCancellationRequested) await ErrorAsync(id, -32800, "Request cancelled."); }
        catch (LspRequestException error) { if (!_lifetime.IsCancellationRequested) await ErrorAsync(id, error.Code, error.Message); }
        catch (Exception error)
        {
            await _log.WriteLineAsync(error.ToString());
            if (!_lifetime.IsCancellationRequested) await ErrorAsync(id, error is JsonException or KeyNotFoundException or InvalidOperationException ? -32602 : -32603, error.Message);
        }
        finally
        {
            if (acquired) _parallelism.Release();
            _requests.TryRemove(key, out _);
            source.Dispose();
        }
    }

    private async Task NotifyAsync(string method, JsonElement parameters)
    {
        if (method == LspMethods.Cancel)
        {
            if (parameters.TryGetProperty("id", out var id) && _requests.TryGetValue(id.GetRawText(), out var source))
                try { source.Cancel(); } catch (ObjectDisposedException) { }
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
            await _connection.WriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics, @params = new { uri, diagnostics = Array.Empty<object>() } }, _lifetime.Token);
        }
        if (document != null) Track(PublishDiagnosticsAsync(document));
    }

    private async Task PublishDiagnosticsAsync(LspDocumentSnapshot document)
    {
        try
        {
            await _parallelism.WaitAsync(_lifetime.Token);
            XamlAnalysis analysis;
            try
            {
                if (!_documents.IsCurrent(document)) return;
                analysis = await Task.Run(() => Volatile.Read(ref _compiler).Analyze(document.Syntax, _lifetime.Token), _lifetime.Token);
            }
            finally { _parallelism.Release(); }
            if (!_documents.IsCurrent(document)) return;
            var diagnostics = analysis.Output.Diagnostics.Select(d => new
            {
                range = LspConversions.Range(document.Syntax, d.Span),
                severity = d.Severity switch { XamlSeverity.Error => 1, XamlSeverity.Warning => 2, XamlSeverity.Info => 3, _ => 4 },
                code = d.Code, source = "XamlG", message = d.Message
            }).ToArray();
            await _connection.WriteAsync(new { jsonrpc = "2.0", method = LspMethods.Diagnostics, @params = new { uri = document.Uri, version = document.Version, diagnostics } }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { await _log.WriteLineAsync("diagnostics: " + error.Message); }
    }

    private ValueTask ErrorAsync(JsonElement? id, int code, string message) => _connection.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, _lifetime.Token);
    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _taskId);
        _tasks[id] = task;
        _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        await _connection.DisposeAsync();
        _parallelism.Dispose();
        _lifetime.Dispose();
    }
}
