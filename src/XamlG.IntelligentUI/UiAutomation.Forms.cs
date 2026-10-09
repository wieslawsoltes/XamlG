using System.Collections.Immutable;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed partial class UiAutomation
{
    private void RegisterForms()
    {
        Add<UiRead, ImmutableArray<UiFormInteraction>>("form_read", "Read active forms, touched/dirty fields, synchronous errors and asynchronous validation status. Does not execute validators.",
            AutomationEffect.Read, (request, owner) => _store.ReadForms(request.Id, owner));
        Add<UiFormCall, UiSnapshot>("form_touch", "Mark an active field touched, using its rendered input key and exact source/state revisions. This is a presentation mutation, not a value change.", AutomationEffect.Edit, _store.TouchForm);
        Add<UiFormCall, UiFormSubmission>("form_submit", "Reveal form errors and identify the first invalid input. A valid form returns a revision-bound action call for the existing review path; no external effect is executed. A registered async validator may be required first.", AutomationEffect.Edit, _store.SubmitForm);
        Add<UiFormCall, UiSnapshot>("form_reset", "Restore active bound form inputs to their initial values and clear touched/submitted/async state atomically. Exact source and state revisions are required.", AutomationEffect.Edit, _store.ResetForm);
        _catalog.Add<UiFormCall, UiSnapshot>("xamlg_ui_form_validate", "Run only the form's trusted application-registered asynchronous validator. Changes to source, data or state invalidate the result. Cannot install code or waive action review.",
            AutomationScope.Agent, AutomationEffect.Edit, async (request, context) =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this); context.CancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(context.PrincipalId)) throw new AutomationException("invalid_principal", "A transport-derived principal is required.");
                try { return await _store.ValidateFormAsync(request, context.PrincipalId, context.CancellationToken).ConfigureAwait(false); }
                catch (UiException error) { throw new AutomationException(error.Code, error.Message); }
            });
    }
}
