using Avalonia.Controls;
using Avalonia.Threading;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Native surface lifetime with keyed reconciliation, local state actions and explicit external intents.
/// All UI interaction is dispatcher-affine; store notifications may originate on any thread.</summary>
public sealed class UiAvaloniaSession : IDisposable
{
    private readonly UiSessionStore _store;
    private readonly string _id, _sessionId;
    private readonly string? _principal;
    private readonly UiAvaloniaCatalog? _catalog;
    private readonly ContentControl _view = new();
    private UiAvaloniaRenderer? _renderer;
    private bool _disposed;
    public Control View => _view;
    public UiSnapshot? Snapshot { get; private set; }
    public string? Diagnostic { get; private set; }
    public event Action? Updated;
    public event Action<string>? Failed;
    /// <summary>Raised only for external actions. The host must prepare and review the intent before executing it.</summary>
    public event Action<UiActionCall>? ActionRequested;

    public UiAvaloniaSession(UiSessionStore store, UiPresentation presentation, string principal, UiAvaloniaCatalog? catalog = null)
        : this(store, presentation, principal, catalog, false) { }
    /// <summary>For a trusted embedding UI only. Never expose this entry point through an agent or transport.</summary>
    public static UiAvaloniaSession CreateLocal(UiSessionStore store, UiPresentation presentation, UiAvaloniaCatalog? catalog = null)
        => new(store, presentation, null, catalog, true);
    private UiAvaloniaSession(UiSessionStore store, UiPresentation presentation, string? principal, UiAvaloniaCatalog? catalog, bool local)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(presentation);
        if (!local && string.IsNullOrWhiteSpace(principal)) throw new ArgumentException("A principal is required.", nameof(principal));
        _store = store; _id = presentation.Id; _sessionId = presentation.SessionId; _principal = principal; _catalog = catalog;
        _store.Changed += OnChanged; _store.Released += OnReleased;
        Refresh();
    }
    public void Refresh()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return;
        try
        {
            var snapshot = _principal == null ? _store.ReadLocal(_id) : _store.Read(_id, _principal);
            if (snapshot == null || snapshot.SessionId != _sessionId)
            { Retire(); Diagnostic = "This intelligent UI session is no longer available."; Updated?.Invoke(); return; }
            if (_renderer == null)
            {
                _renderer = new(_catalog, _store.Compiler.Catalog);
                _renderer.StateChanged += ChangeState;
                _renderer.ActionRequested += OnAction;
                _view.Content = _renderer.View;
            }
            _renderer.Apply(snapshot); Snapshot = snapshot; Diagnostic = null; Updated?.Invoke();
        }
        catch (UiException error) { Diagnostic = error.Message; Failed?.Invoke(error.Message); }
    }
    public void ChangeState(UiStateChange change)
    {
        Dispatcher.UIThread.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (Snapshot == null || change.Id != _id) throw new UiException("unknown_surface", "The native session is retired.");
        try
        {
            if (_principal == null) _store.ChangeStateLocal(change); else _store.ChangeState(change, _principal);
            Refresh();
        }
        catch (UiException error) { Refresh(); Diagnostic = error.Message; Failed?.Invoke(error.Message); }
    }
    private void OnAction(UiActionCall call)
    {
        if (_disposed || Snapshot == null) return;
        try
        {
            // Routing must not evaluate expressions. In approved C# mode even preparing an
            // expression can execute code. The owned mutation validates and evaluates once.
            if (UiActionRouting.IsStateAction(Snapshot, call))
            {
                if (_principal == null) _store.ApplyStateActionLocal(call); else _store.ApplyStateAction(call, _principal);
                Refresh();
            }
            else ActionRequested?.Invoke(call);
        }
        catch (UiException error) { Refresh(); Diagnostic = error.Message; Failed?.Invoke(error.Message); }
    }
    private void OnChanged(UiSnapshot snapshot) { if (snapshot.Id == _id) ScheduleRefresh(); }
    private void OnReleased(string id) { if (id == _id) ScheduleRefresh(); }
    private void ScheduleRefresh()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh);
    }
    private void Retire()
    {
        Snapshot = null; _view.Content = null;
        _renderer?.Dispose(); _renderer = null;
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return; _disposed = true;
        _store.Changed -= OnChanged; _store.Released -= OnReleased;
        Retire(); Updated = null; Failed = null; ActionRequested = null;
    }
}
