using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    /// <summary>All expressions read pre-action state. Candidate state and render commit atomically.</summary>
    public UiSnapshot ApplyStateAction(UiActionCall request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var intent = PrepareAction(request, principal);
            if (intent.Kind != "state" || intent.Arguments is not { } patch)
                throw new UiException("invalid_action", "Only a declared state action can change local state.");
            var values = entry.Snapshot.State.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
            foreach (var field in patch.EnumerateObject())
            {
                if (!values.TryGetValue(field.Name, out var old)) throw new UiException("unknown_state", "The action targets an undeclared state key: " + field.Name);
                if (!SameStateType(old, field.Value) && old.ValueKind != JsonValueKind.Null && field.Value.ValueKind != JsonValueKind.Null)
                    throw new UiException("invalid_state", "An action cannot change a state slot's declared type.");
                values[field.Name] = field.Value;
            }
            var state = ValidateState(JsonSerializer.SerializeToElement(values));
            var roots = entry.Template.Render(state, entry.Snapshot.Data, entry.Snapshot.Roots);
            ValidateActionReferences(roots, entry.Snapshot.Actions);
            if (JsonElement.DeepEquals(state, entry.Snapshot.State)) return entry.Snapshot;
            snapshot = entry.Snapshot with { State = state, StateRevision = checked(entry.Snapshot.StateRevision + 1),
                Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    /// <summary>Trusted embedding UI only; never expose this entry point to a transport.</summary>
    public UiSnapshot ApplyStateActionLocal(UiActionCall request) => ApplyStateAction(request, LocalPrincipal(request.Id));
}
