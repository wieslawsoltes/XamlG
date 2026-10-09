using XamlG.IntelligentUI;
using XamlG.IntelligentUI.Avalonia;
using Microsoft.JSInterop;
using Dispatcher = Avalonia.Threading.Dispatcher;

namespace XamlG.Playground.Components;

public partial class IntelligentUiGuest
{
    private UiAvaloniaFormBehavior? _forms;
    private readonly HashSet<string> _submittingForms = new(StringComparer.Ordinal);
    private void InitializeFormBehavior()
    {
        _forms = new(_renderer!);
        _forms.TouchRequested += FormTouched;
        _forms.SubmitRequested += FormSubmitted;
    }
    private void ApplyNative(UiSnapshot snapshot) => _forms!.Apply(snapshot);

    private async void FormTouched(UiFormCall call)
    {
        var epoch = _surfaceEpoch;
        try
        {
            await _mutations.WaitAsync(_lifetime.Token);
            try
            {
                if (!IsCurrent(epoch) || _snapshot == null || _snapshot.Id != call.Id || _snapshot.Revision != call.ExpectedRevision) return;
                var form = UiSessionStore.Flatten(_snapshot.Roots).FirstOrDefault(node => node.FormState?.Id == call.FormKey)?.FormState;
                if (form == null || !form.Fields.Any(input => input.Key == call.FieldKey && !input.Touched)) return;
                call = call with { ExpectedStateRevision = _snapshot.StateRevision };
                var next = Mode == "execution" ? _executionStore!.TouchForm(call, "approved-execution")
                    : await _bridge!.InvokeAsync<UiSnapshot>("tool", _lifetime.Token, "xamlg_ui_form_touch", call);
                if (IsCurrent(epoch)) await ReceiveSnapshot(next);
            }
            finally { _mutations.Release(); }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (IsCurrent(epoch)) await ReceiveError(error.Message); }
    }

    private async void FormSubmitted(UiFormCall call)
    {
        var epoch = _surfaceEpoch;
        var identity = epoch + ":" + call.FormKey;
        if (!_submittingForms.Add(identity)) return;
        try
        {
            var result = await SubmitGuestFormAsync(call, epoch);
            if (result == null || !IsCurrent(epoch)) return;
            if (result.RequiresValidation)
            {
                var request = call with { ExpectedRevision = result.Snapshot.Revision, ExpectedStateRevision = result.Snapshot.StateRevision };
                var next = Mode == "execution" ? _executionStore!.StartFormValidation(request, "approved-execution")
                    : await _bridge!.InvokeAsync<UiSnapshot>("tool", _lifetime.Token, "xamlg_ui_form_validate_start", request);
                if (!IsCurrent(epoch)) return;
                await ReceiveSnapshot(next);
                var validation = UiSessionStore.Flatten(next.Roots).FirstOrDefault(node => node.FormState?.Id == call.FormKey)?.FormState;
                if (validation == null) return;
                var stamp = validation.Stamp;
                // Poll only inert status. Do not hold the mutation semaphore while validation runs.
                while (validation.Pending)
                {
                    await Task.Delay(200, _lifetime.Token);
                    if (!IsCurrent(epoch)) return;
                    var current = Mode == "execution" ? _executionStore!.Read(next.Id, "approved-execution")
                        : await _bridge!.InvokeAsync<UiSnapshot>("tool", _lifetime.Token, "xamlg_ui_read", new UiRead(next.Id));
                    if (!IsCurrent(epoch)) return;
                    await ReceiveSnapshot(current);
                    validation = UiSessionStore.Flatten(current.Roots).FirstOrDefault(node => node.FormState?.Id == call.FormKey)?.FormState;
                    if (validation == null || validation.Stamp != stamp || current.Revision != next.Revision) return;
                    next = current;
                }
                if (!validation.IsValid) return;
                result = await SubmitGuestFormAsync(call with { ExpectedRevision = next.Revision, ExpectedStateRevision = next.StateRevision }, epoch);
            }
            if (result == null || !IsCurrent(epoch)) return;
            await Dispatcher.UIThread.InvokeAsync(() => { if (IsCurrent(epoch)) _forms?.Focus(result.FocusKey); });
            if (result.Action != null) DispatchAction(result.Action);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { if (IsCurrent(epoch)) await ReceiveError(error.Message); }
        finally { _submittingForms.Remove(identity); }
    }

    private async Task<UiFormSubmission?> SubmitGuestFormAsync(UiFormCall request, long epoch)
    {
        await _mutations.WaitAsync(_lifetime.Token);
        try
        {
            if (!IsCurrent(epoch) || _snapshot == null || _snapshot.Id != request.Id || _snapshot.Revision != request.ExpectedRevision) return null;
            request = request with { ExpectedStateRevision = _snapshot.StateRevision };
            var result = Mode == "execution" ? _executionStore!.SubmitForm(request, "approved-execution")
                : await _bridge!.InvokeAsync<UiFormSubmission>("tool", _lifetime.Token, "xamlg_ui_form_submit", request);
            if (!IsCurrent(epoch)) return null;
            await ReceiveSnapshot(result.Snapshot);
            if (Mode == "mcp") await PublishContextAsync(result.Snapshot);
            return result;
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
