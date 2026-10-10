using Avalonia.Controls;
using Avalonia.Threading;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Native keyed session with local actions, form workflows and reviewed external intents.</summary>
public sealed class UiAvaloniaSession : IDisposable
{
    private readonly UiSessionStore _store;
    private readonly string _id, _sessionId;
    private readonly string? _principal;
    private readonly UiAvaloniaCatalog? _catalog;
    private readonly ContentControl _view = new();
    private readonly CancellationTokenSource _lifetime = new();
    private UiAvaloniaRenderer? _renderer;
    private UiAvaloniaFormBehavior? _forms;
    private bool _disposed;
    public Control View => _view;
    public UiSnapshot? Snapshot { get; private set; }
    public string? Diagnostic { get; private set; }
    public event Action? Updated;
    public event Action<string>? Failed;
    /// <summary>External actions still require host preparation and review before execution.</summary>
    public event Action<UiActionCall>? ActionRequested;
    public UiAvaloniaSession(UiSessionStore store, UiPresentation presentation, string principal, UiAvaloniaCatalog? catalog = null)
        : this(store, presentation, principal, catalog, false) { }
    public static UiAvaloniaSession CreateLocal(UiSessionStore store, UiPresentation presentation, UiAvaloniaCatalog? catalog = null)
        => new(store, presentation, null, catalog, true);
    private UiAvaloniaSession(UiSessionStore store, UiPresentation presentation, string? principal, UiAvaloniaCatalog? catalog, bool local)
    {
        Dispatcher.UIThread.VerifyAccess(); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(presentation);
        if (!local && string.IsNullOrWhiteSpace(principal)) throw new ArgumentException("A principal is required.", nameof(principal));
        _store = store; _id = presentation.Id; _sessionId = presentation.SessionId; _principal = principal; _catalog = catalog;
        _store.Changed += OnChanged; _store.Released += OnReleased; Refresh();
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
                _renderer.StateChanged += ChangeState; _renderer.ActionRequested += OnAction;
                _forms = new(_renderer); _forms.TouchRequested += OnTouch; _forms.SubmitRequested += OnSubmit;
                _view.Content = _renderer.View;
            }
            _forms!.Apply(snapshot); Snapshot = snapshot; Diagnostic = null; Updated?.Invoke();
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
        catch (UiException error) { Report(error); }
    }
    private void OnTouch(UiFormCall call)
    {
        if (_disposed || Snapshot == null) return;
        try { if (_principal == null) _store.TouchFormLocal(call); else _store.TouchForm(call, _principal); }
        catch (UiException error) { if (error.Code == "revision_conflict") Refresh(); else Report(error); }
    }
    private async void OnSubmit(UiFormCall call)
    {
        try { await SubmitAsync(call); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (UiException error) { await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed) Report(error); }); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed) { Diagnostic = "Form submission failed."; Failed?.Invoke(Diagnostic); } }); }
    }
    public async Task<UiFormSubmission> SubmitAsync(UiFormCall call, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (Snapshot?.SessionId != _sessionId || call.Id != _id) throw new UiException("unknown_surface", "The native session is retired.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var result = _principal == null ? _store.SubmitFormLocal(call) : _store.SubmitForm(call, _principal);
        if (result.RequiresValidation)
        {
            var pending = call with { ExpectedRevision = result.Snapshot.Revision, ExpectedStateRevision = result.Snapshot.StateRevision };
            var next = _principal == null ? await _store.ValidateFormLocalAsync(pending, lifetime.Token)
                : await _store.ValidateFormAsync(pending, _principal, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var current = pending with { ExpectedRevision = next.Revision, ExpectedStateRevision = next.StateRevision };
            result = _principal == null ? _store.SubmitFormLocal(current) : _store.SubmitForm(current, _principal);
        }
        void Complete()
        {
            lifetime.Token.ThrowIfCancellationRequested(); Refresh(); _forms?.Focus(result.FocusKey);
            if (result.Action != null) DispatchAction(result.Action);
        }
        // Synchronous forms preserve normal routed-event ordering and the existing host contract.
        if (Dispatcher.UIThread.CheckAccess()) Complete(); else await Dispatcher.UIThread.InvokeAsync(Complete);
        return result;
    }
    private void OnAction(UiActionCall call)
    {
        if (_disposed || Snapshot == null) return;
        var form = UiSessionStore.Flatten(Snapshot.Roots).FirstOrDefault(node => node.Key == call.NodeKey)?.Form;
        if (form is { Role: "submit" }) { OnSubmit(new(call.Id, call.ExpectedRevision, call.ExpectedStateRevision, form.Id)); return; }
        DispatchAction(call);
    }
    private void DispatchAction(UiActionCall call)
    {
        if (_disposed || Snapshot == null) return;
        try
        {
            if (UiActionRouting.IsStateAction(Snapshot, call))
            {
                if (_principal == null) _store.ApplyStateActionLocal(call); else _store.ApplyStateAction(call, _principal);
                Refresh();
            }
            else ActionRequested?.Invoke(call);
        }
        catch (UiException error) { Report(error); }
    }
    private void Report(UiException error) { Refresh(); Diagnostic = error.Message; Failed?.Invoke(error.Message); }
    private void OnChanged(UiSnapshot snapshot) { if (snapshot.Id == _id) ScheduleRefresh(); }
    private void OnReleased(string id) { if (id == _id) ScheduleRefresh(); }
    private void ScheduleRefresh()
    { if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh); }
    private void Retire()
    {
        _forms?.Dispose(); _forms = null;
        Snapshot = null; _view.Content = null; _renderer?.Dispose(); _renderer = null;
    }
    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess(); if (_disposed) return; _disposed = true; _lifetime.Cancel();
        _store.Changed -= OnChanged; _store.Released -= OnReleased;
        Retire(); _lifetime.Dispose(); Updated = null; Failed = null; ActionRequested = null;
    }
}
