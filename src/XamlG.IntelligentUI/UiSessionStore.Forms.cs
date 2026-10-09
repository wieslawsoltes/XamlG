using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    private readonly Dictionary<string, UiAsyncFormValidator> _formValidators = new(StringComparer.Ordinal);
    /// <summary>Trusted application registration, never a model or transport API.</summary>
    public void RegisterFormValidator(string name, UiAsyncFormValidator validator)
    {
        UiJson.Identifier(name, "Validator name"); ArgumentNullException.ThrowIfNull(validator);
        lock (_gate)
            if (_formValidators.Count >= 64 || !_formValidators.TryAdd(name, validator))
                throw new UiException("invalid_validator", "The validator is already registered or the registry is full.");
    }
    public ImmutableArray<UiFormInteraction> ReadForms(string id, string principal)
    {
        lock (_gate) return UiFormProjection.Active(Get(id, principal).Snapshot.Roots)
            .Where(node => node.FormState != null).Select(node => node.FormState!).ToImmutableArray();
    }
    public UiSnapshot TouchForm(UiFormCall request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var form = FindForm(entry.Snapshot, request.FormKey);
            if (request.FieldKey == null || !form.Fields.Any(input => input.Key == request.FieldKey))
                throw new UiException("invalid_form", "The field is not an active member of this form.");
            if (form.Fields.Single(input => input.Key == request.FieldKey).Touched) return entry.Snapshot;
            snapshot = CommitForm(entry, form with { Fields = form.Fields.Select(input => input.Key == request.FieldKey ? input with { Touched = true } : input).ToImmutableArray() });
        }
        Notify(snapshot); return snapshot;
    }
    /// <summary>Reveal errors and return a revision-bound action; never execute an external effect.</summary>
    public UiFormSubmission SubmitForm(UiFormCall request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        UiSnapshot snapshot; UiActionCall? action = null; string? focus; bool validation;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var form = FindForm(entry.Snapshot, request.FormKey);
            snapshot = form.Submitted ? entry.Snapshot : CommitForm(entry, form with { Submitted = true });
            form = FindForm(snapshot, request.FormKey);
            var button = Flatten(snapshot.Roots).FirstOrDefault(node => node.Form is { Role: "submit", AuthorEnabled: true } role &&
                role.Id == request.FormKey && node.ActionId != null && !Disabled(node, snapshot.Roots));
            if (form.IsValid && button != null) action = new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, button.Key);
            focus = form.FirstInvalidKey;
            validation = button != null && form.Error == null && focus == null && form.Validator != null &&
                (!form.Validated || form.AsyncError != null) && !form.Pending;
        }
        Notify(snapshot); return new(snapshot, action, focus, validation);
    }
    public UiSnapshot ResetForm(UiFormCall request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var form = FindForm(entry.Snapshot, request.FormKey);
            var values = entry.Snapshot.State.EnumerateObject().ToDictionary(input => input.Name, input => input.Value, StringComparer.Ordinal);
            foreach (var group in form.Fields.Where(input => input.StateKey != null).GroupBy(input => input.StateKey!, StringComparer.Ordinal))
            {
                var initial = group.First().InitialValue;
                if (group.Any(input => !JsonElement.DeepEquals(initial, input.InitialValue)))
                    throw new UiException("invalid_form", "Shared input bindings have conflicting reset values.");
                values[group.Key] = initial;
            }
            var state = ValidateState(JsonSerializer.SerializeToElement(values));
            var history = form with { Submitted = false, Pending = false, Validated = false, AsyncError = null, ValidationId = null,
                Fields = form.Fields.Select(input => input with { Touched = false }).ToImmutableArray() };
            snapshot = CommitForm(entry, history, state);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot TouchFormLocal(UiFormCall request) => TouchForm(request, LocalPrincipal(request.Id));
    public UiFormSubmission SubmitFormLocal(UiFormCall request) => SubmitForm(request, LocalPrincipal(request.Id));
    public UiSnapshot ResetFormLocal(UiFormCall request) => ResetForm(request, LocalPrincipal(request.Id));
    private static UiFormInteraction FindForm(UiSnapshot snapshot, string key)
        => UiFormProjection.Active(snapshot.Roots).FirstOrDefault(node => node.FormState?.Id == key)?.FormState
            ?? throw new UiException("invalid_form", "The form is not available in the current view.");
    private UiSnapshot CommitForm(Entry entry, UiFormInteraction form, JsonElement? state = null)
    {
        var history = UiFormProjection.ReplaceHistory(entry.Snapshot.Roots, form);
        var values = state ?? entry.Snapshot.State;
        var roots = entry.Template.Render(values, entry.Snapshot.Data, history);
        ValidateActionReferences(roots, entry.Snapshot.Actions);
        var snapshot = entry.Snapshot with { State = values, StateRevision = checked(entry.Snapshot.StateRevision + 1),
            Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
        _entries[snapshot.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        return snapshot;
    }
}
