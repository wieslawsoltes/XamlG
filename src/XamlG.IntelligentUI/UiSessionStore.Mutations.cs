using System.Text.Json;
using System.Text.Json.Nodes;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    public UiSnapshot ChangeState(UiStateChange request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            if (!entry.Snapshot.State.TryGetProperty(request.Key, out var old)) throw new UiException("unknown_state", "Unknown state key.");
            if (!SameStateType(old, request.Value) && old.ValueKind != JsonValueKind.Null && request.Value.ValueKind != JsonValueKind.Null)
                throw new UiException("invalid_state", "Input state cannot change its declared type.");
            var inputs = Flatten(entry.Snapshot.Roots).Where(node => node.StateKey == request.Key).ToArray();
            if (inputs.Length == 0 || inputs.All(node => Disabled(node, entry.Snapshot.Roots) || node.Properties.TryGetValue("IsReadOnly", out var readOnly) && readOnly.GetBoolean()))
                throw new UiException("invalid_state", "No enabled writable input exposes this state key.");
            foreach (var input in inputs)
                if (input.Type == "TextBox" && input.Properties.TryGetValue("MaxLength", out var maximum) && request.Value.ValueKind == JsonValueKind.String && request.Value.GetString()!.Length > maximum.GetDecimal())
                    throw new UiException("invalid_state", "Input exceeds its declared MaxLength.");
            var model = JsonNode.Parse(entry.Snapshot.State.GetRawText())!.AsObject(); model[request.Key] = JsonNode.Parse(request.Value.GetRawText());
            var state = ValidateState(JsonSerializer.SerializeToElement(model));
            var roots = entry.Template.Render(state, entry.Snapshot.Data, entry.Snapshot.Roots); ValidateActionReferences(roots, entry.Snapshot.Actions);
            snapshot = entry.Snapshot with { State = state, StateRevision = checked(entry.Snapshot.StateRevision + 1), Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot ChangeData(UiDataChange request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = UiJson.Object(request.Data, Compiler.Limits.DataBytes, "Data"); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The surface changed before data arrived.");
            var roots = entry.Template.Render(entry.Snapshot.State, data, entry.Snapshot.Roots); ValidateActionReferences(roots, entry.Snapshot.Actions);
            snapshot = entry.Snapshot with { Revision = checked(++_revision), Data = data, Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot ChangeStateLocal(UiStateChange request) => ChangeState(request, LocalPrincipal(request.Id));
    public UiActionIntent PrepareActionLocal(UiActionCall request) => PrepareAction(request, LocalPrincipal(request.Id));
    private static bool SameStateType(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind ||
        a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private JsonElement MergeState(JsonElement? old, JsonElement? initial)
    {
        if (initial == null) return ValidateState(old ?? UiJson.Empty);
        var defaults = ValidateState(initial.Value); var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var value in defaults.EnumerateObject())
        {
            var previous = old is { } oldState && oldState.TryGetProperty(value.Name, out var found) ? found : value.Value;
            result.Add(value.Name, SameStateType(previous, value.Value) || previous.ValueKind == JsonValueKind.Null || value.Value.ValueKind == JsonValueKind.Null ? previous : value.Value);
        }
        return ValidateState(JsonSerializer.SerializeToElement(result));
    }
    private JsonElement ValidateState(JsonElement state)
    {
        state = UiJson.Object(state, Compiler.Limits.DataBytes, "State");
        if (state.EnumerateObject().Count() > Compiler.Limits.StateKeys) throw new UiException("state_limit", "Too many state keys.");
        foreach (var field in state.EnumerateObject())
        {
            UiJson.Identifier(field.Name, "State key");
            if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Length > Compiler.Limits.TextCharacters ||
                field.Value.ValueKind == JsonValueKind.Number && !field.Value.TryGetDecimal(out _))
                throw new UiException("invalid_state", "State slot exceeds its bounds.");
        }
        return state;
    }
}
