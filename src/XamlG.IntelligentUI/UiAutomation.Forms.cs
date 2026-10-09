using System.Collections.Immutable;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed partial class UiAutomation
{
    private void RegisterForms()
    {
        Add<UiRead, ImmutableArray<UiFormInteraction>>("form_read", "Read active forms, touched/dirty fields, errors and async status without executing validators.", AutomationEffect.Read,
            (request, owner) => _store.ReadForms(request.Id, owner));
        Add<UiFormCall, UiSnapshot>("form_touch", "Mark an active input key touched at exact source/state revisions, without changing its value.", AutomationEffect.Edit, _store.TouchForm);
        Add<UiFormCall, UiFormSubmission>("form_submit", "Reveal form errors and identify the first invalid input. A valid form returns a revision-bound action call for the existing review path, not an executed effect. A trusted async validator may be required first.", AutomationEffect.Edit, _store.SubmitForm);
        Add<UiFormCall, UiSnapshot>("form_reset", "Atomically restore active bound form inputs to initial values and clear touched/submitted/async status at exact revisions.", AutomationEffect.Edit, _store.ResetForm);
        Add<UiFormCall, UiSnapshot>("form_validate_start", "Start the form's trusted registered async validator and return its committed pending snapshot. Follow ui_read or resource updates for completion; input remains editable and changed values invalidate the result. Does not install code or grant effect permissions.", AutomationEffect.Edit, _store.StartFormValidation);
        _catalog.Add<UiFormCall, UiSnapshot>("xamlg_ui_form_validate", "Await the form's trusted application-registered async validator. Source, data, reset and value changes invalidate its result. Cannot install code or waive action review.",
            AutomationScope.Agent, AutomationEffect.Edit, async (request, context) =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this); context.CancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(context.PrincipalId)) throw new AutomationException("invalid_principal", "A transport-derived principal is required.");
                try { return await _store.ValidateFormAsync(request, context.PrincipalId, context.CancellationToken).ConfigureAwait(false); }
                catch (UiException error) { throw new AutomationException(error.Code, error.Message); }
            });
    }
}
