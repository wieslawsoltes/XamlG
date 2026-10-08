using Microsoft.JSInterop;

namespace XamlG.Playground.Editing;

/// <summary>Application-scoped editor interop owner. Replacing an editor never disposes the
/// shared ES module. A unique JS facade isolates its reference lifetime from other import users.</summary>
public sealed class EditorInteropModule(IJSRuntime javaScript) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<EditorInteropSession> _sessions = new();
    private Task<IJSObjectReference>? _import;
    private IJSObjectReference? _sourceModule;
    private Task? _disposal;
    private bool _retired;

    public EditorInteropSession Create(object?[] arguments, IDisposable callback)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            _import ??= ImportAsync();
            var session = new EditorInteropSession(_import, arguments.ToArray(), callback, Release);
            _sessions.Add(session);
            return session;
        }
    }

    private async Task<IJSObjectReference> ImportAsync()
    {
        _sourceModule = await javaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "studio.js");
        return await _sourceModule.InvokeAsync<IJSObjectReference>("createEditorInterop");
    }

    private void Release(EditorInteropSession session)
    {
        lock (_gate) _sessions.Remove(session);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal != null) return new(_disposal);
            _retired = true;
            // Snapshot first: synchronously completed cleanup removes sessions from the set.
            var sessions = _sessions.ToArray();
            var pending = sessions.Select(session => session.DisposeAsync().AsTask()).ToArray();
            _disposal = DisposeCoreAsync(pending);
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task[] pending)
    {
        var errors = new List<Exception>();
        try { await Task.WhenAll(pending); }
        catch (Exception error) { errors.Add(error); }
        IJSObjectReference? facade = null;
        if (_import != null)
        {
            try { facade = await _import; }
            catch { /* Initialization failure is already observed by the creating component. */ }
        }
        foreach (var reference in new[] { facade, _sourceModule })
        {
            if (reference == null) continue;
            try { await reference.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Editor interop cleanup failed.", errors);
    }
}
