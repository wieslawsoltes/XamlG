using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;
using Microsoft.JSInterop;
using Dispatcher = Avalonia.Threading.Dispatcher;

namespace XamlG.Playground.Components;

public partial class IntelligentUiGuest
{
    private UiAvaloniaFormBehavior? _forms;
    private readonly Dictionary<string, FormOperation> _submittingForms = new(StringComparer.Ordinal);
    private sealed record FormOperation(string Stamp);
    private void InitializeFormBehavior()
    {
        _forms = new(_renderer!); _forms.TouchRequested += FormTouched; _forms.SubmitRequested += FormSubmitted;
    }
    private void ApplyNative(UiSnapshot snapshot) => _forms!.Apply(snapshot);
    private static UiFormInteraction? GuestForm(UiSnapshot snapshot, string key)
        => UiSessionStore.Flatten(snapshot.Roots).FirstOrDefault(node => node.FormState?.Id == key)?.FormState;
    private async ValueTask<T> FormBackendAsync<T>(string method, object request)
        => Mode == "execution" ? await ExecuteCommandAsync<T>(method, request)
            : await _bridge!.InvokeAsync<T>("tool", _lifetime.Token, "xamlg_ui_" + method, request);
    private async void FormTouched(UiFormCall call)
    {
        var epoch = _surfaceEpoch;
        try
        {
            await _mutations.WaitAsync(_lifetime.Token);
            try
            {
                if (!IsCurrent(epoch) || _snapshot == null || _snapshot.Id != call.Id || _snapshot.Revision != call.ExpectedRevision) return;
                var form = GuestForm(_snapshot, call.FormKey);
                if (form == null || !form.Fields.Any(input => input.Key == call.FieldKey && !input.Touched)) return;
                call = call with { ExpectedStateRevision = _snapshot.StateRevision };
                var next = await FormBackendAsync<UiSnapshot>("form_touch", call);
                if (IsCurrent(epoch)) await ReceiveSnapshot(next);
            }
            finally { _mutations.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (IsCurrent(epoch)) await ReceiveError(error.Message); }
    }
    private async void FormSubmitted(UiFormCall call)
    {
        var epoch = _surfaceEpoch; var identity = epoch + ":" + call.FormKey;
        FormOperation? operation = null;
        bool Owns() => operation != null && _submittingForms.TryGetValue(identity, out var current) && ReferenceEquals(operation, current);
        bool Active() => IsCurrent(epoch) && Owns();
        try
        {
            UiFormSubmission? result;
            await _mutations.WaitAsync(_lifetime.Token);
            try
            {
                if (!IsCurrent(epoch) || _snapshot == null || _snapshot.Id != call.Id || _snapshot.Revision != call.ExpectedRevision) return;
                var form = GuestForm(_snapshot, call.FormKey); if (form == null) return;
                if (_submittingForms.TryGetValue(identity, out var existing) && existing.Stamp == form.Stamp) return;
                operation = new(form.Stamp); _submittingForms[identity] = operation;
                var current = call with { ExpectedStateRevision = _snapshot.StateRevision };
                result = await FormBackendAsync<UiFormSubmission>("form_submit", current);
                if (!Active()) return; await ReceiveSnapshot(result.Snapshot);
            }
            finally { _mutations.Release(); }
            if (result.RequiresValidation)
            {
                UiSnapshot next;
                await _mutations.WaitAsync(_lifetime.Token);
                try
                {
                    if (!Active() || _snapshot == null || _snapshot.Revision != call.ExpectedRevision || GuestForm(_snapshot, call.FormKey)?.Stamp != operation.Stamp) return;
                    var request = call with { ExpectedStateRevision = _snapshot.StateRevision };
                    next = await FormBackendAsync<UiSnapshot>("form_validate_start", request);
                    if (!Active()) return; await ReceiveSnapshot(next);
                }
                finally { _mutations.Release(); }
                var validation = GuestForm(next, call.FormKey); if (validation == null) return;
                var nonce = validation.ValidationId; var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (validation.Pending)
                {
                    if (DateTimeOffset.UtcNow >= deadline) throw new UiException("validation_timeout", "Validation did not complete before the UI deadline. Refresh its status.");
                    await Task.Delay(200, _lifetime.Token); if (!Active()) return;
                    var current = await FormBackendAsync<UiSnapshot>("read", new UiRead(next.Id));
                    if (!Active()) return; await ReceiveSnapshot(current);
                    validation = GuestForm(current, call.FormKey);
                    if (validation == null || validation.Stamp != operation.Stamp || current.Revision != next.Revision ||
                        validation.Pending && validation.ValidationId != nonce) return;
                    next = current;
                }
                if (!validation.IsValid) return;
                result = await SubmitGuestFormAsync(call with { ExpectedRevision = next.Revision, ExpectedStateRevision = next.StateRevision }, epoch, operation.Stamp, Active);
            }
            if (result == null || !Active()) return;
            await Dispatcher.UIThread.InvokeAsync(() => { if (Active()) _forms?.Focus(result.FocusKey); });
            if (Mode == "mcp") await PublishContextAsync(result.Snapshot);
            if (result.Action != null && Active()) DispatchAction(result.Action);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (Active()) await ReceiveError(error.Message); }
        finally { if (Owns()) _submittingForms.Remove(identity); }
    }
    private async Task<UiFormSubmission?> SubmitGuestFormAsync(UiFormCall request, long epoch, string stamp, Func<bool> active)
    {
        await _mutations.WaitAsync(_lifetime.Token);
        try
        {
            if (!IsCurrent(epoch) || !active() || _snapshot == null || _snapshot.Id != request.Id || _snapshot.Revision != request.ExpectedRevision || GuestForm(_snapshot, request.FormKey)?.Stamp != stamp) return null;
            request = request with { ExpectedStateRevision = _snapshot.StateRevision };
            var result = await FormBackendAsync<UiFormSubmission>("form_submit", request);
            if (!active()) return null; await ReceiveSnapshot(result.Snapshot); return result;
        }
        finally { _mutations.Release(); }
    }
    private void ActionRequested(UiActionCall call)
    {
        var form = _snapshot == null ? null : UiSessionStore.Flatten(_snapshot.Roots).FirstOrDefault(node => node.Key == call.NodeKey)?.Form;
        if (form is { Role: "submit" }) FormSubmitted(new(call.Id, call.ExpectedRevision, call.ExpectedStateRevision, form.Id));
        else DispatchAction(call);
    }
}
