using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XamlG.IntelligentUI;

/// <summary>Owner-scoped UI sessions with atomic source/state/data publication. Revisions never
/// reset when a surface is released. Persistence captures declarations, not executable objects or authority.</summary>
public sealed partial class UiSessionStore(UiCompiler? compiler = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _revision, _lifetime, _generation;
    public UiCompiler Compiler { get; } = compiler ?? new();
    public long Generation { get { lock (_gate) return _generation; } }
    public event Action<UiSnapshot>? Changed;
    public event Action<string>? Released;
    public UiSnapshot Publish(UiPublish request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request); Principal(principal); UiJson.Identifier(request.Id, "Surface ID");
        if (request.Xaml == null) throw new UiException("invalid_xaml", "XAML is required.");
        if (request.ExpectedRevision < 0 || request.Sequence < 1) throw new UiException("revision_conflict", "Invalid revision or stream sequence.");
        long lifetime; lock (_gate) lifetime = _lifetime;
        var compiled = Compiler.Compile(request.Xaml, request.IsFinal);
        if (!compiled.Success) throw new UiException("compilation_failed", string.Join("\n", compiled.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var actions = ValidateActions(request.Actions ?? []);
        if (request.FallbackMarkdown?.Length > Compiler.Limits.TextCharacters) throw new UiException("text_limit", "Fallback exceeds the text limit.");
        UiSnapshot snapshot;
        lock (_gate)
        {
            if (lifetime != _lifetime) throw new UiException("workspace_changed", "The UI workspace was retired during compilation.");
            _entries.TryGetValue(request.Id, out var current);
            if (current != null) Owner(current, principal);
            if ((current?.Snapshot.Revision ?? 0) != request.ExpectedRevision || request.Sequence != (current?.Snapshot.Sequence ?? 0) + 1) throw new UiException("revision_conflict", "Re-read the surface revision and next stream sequence.");
            if (current == null && _entries.Count >= Compiler.Limits.Surfaces) throw new UiException("surface_limit", "Release an existing surface before creating another.");
            var state = MergeState(current?.Snapshot.State, request.InitialState);
            var data = UiJson.Object(request.Data ?? current?.Snapshot.Data, Compiler.Limits.DataBytes, "Data");
            var roots = compiled.Template!.Render(state, data); ValidateActionReferences(roots, actions);
            snapshot = new(request.Id, checked(++_revision), current?.Snapshot.StateRevision ?? 0, request.Sequence, request.IsFinal,
                request.Xaml, state, data, roots, actions, compiled.Diagnostics, Fallback(roots, request.FallbackMarkdown), current?.Snapshot.SessionId ?? Guid.NewGuid().ToString("N"));
            if (current != null && !JsonElement.DeepEquals(current.Snapshot.State, state)) snapshot = snapshot with { StateRevision = checked(snapshot.StateRevision + 1) };
            _entries[request.Id] = new(principal, compiled.Template, snapshot, request.FallbackMarkdown); _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot Read(string id, string principal) { lock (_gate) return Get(id, principal).Snapshot; }
    public IReadOnlyList<UiPresentation> List(string principal)
    {
        Principal(principal); lock (_gate) return _entries.Values.Where(entry => entry.Principal == principal).OrderBy(entry => entry.Snapshot.Id, StringComparer.Ordinal).Select(entry => UiPresentation.From(entry.Snapshot)).ToArray();
    }
    /// <summary>Trusted local presentation only. Never expose this owner-bypassing method to agents or transports.</summary>
    public UiSnapshot? ReadLocal(string id) { lock (_gate) return _entries.GetValueOrDefault(id)?.Snapshot; }
    public IReadOnlyList<UiSnapshot> ListLocal() { lock (_gate) return _entries.Values.Select(entry => entry.Snapshot).ToArray(); }
    public UiSnapshot ChangeState(UiStateChange request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            if (!entry.Snapshot.State.TryGetProperty(request.Key, out var old)) throw new UiException("unknown_state", "Unknown state key.");
            if (!SameStateType(old, request.Value) && old.ValueKind != JsonValueKind.Null && request.Value.ValueKind != JsonValueKind.Null) throw new UiException("invalid_state", "Input state cannot change its declared type.");
            var inputs = Flatten(entry.Snapshot.Roots).Where(node => node.StateKey == request.Key).ToArray();
            if (inputs.Length == 0 || inputs.All(node => Disabled(node, entry.Snapshot.Roots) || node.Properties.TryGetValue("IsReadOnly", out var readOnly) && readOnly.GetBoolean())) throw new UiException("invalid_state", "No enabled writable input exposes this state key.");
            foreach (var input in inputs)
                if (input.Type == "TextBox" && input.Properties.TryGetValue("MaxLength", out var maximum) && request.Value.ValueKind == JsonValueKind.String && request.Value.GetString()!.Length > maximum.GetDecimal()) throw new UiException("invalid_state", "Input exceeds its declared MaxLength.");
            var model = JsonNode.Parse(entry.Snapshot.State.GetRawText())!.AsObject(); model[request.Key] = JsonNode.Parse(request.Value.GetRawText());
            var state = ValidateState(JsonSerializer.SerializeToElement(model)); var roots = entry.Template.Render(state, entry.Snapshot.Data); ValidateActionReferences(roots, entry.Snapshot.Actions);
            snapshot = entry.Snapshot with { State = state, StateRevision = checked(entry.Snapshot.StateRevision + 1), Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot ChangeData(UiDataChange request, string principal)
    {
        var data = UiJson.Object(request.Data, Compiler.Limits.DataBytes, "Data"); UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The surface changed before data arrived.");
            var roots = entry.Template.Render(entry.Snapshot.State, data); ValidateActionReferences(roots, entry.Snapshot.Actions);
            snapshot = entry.Snapshot with { Revision = checked(++_revision), Data = data, Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
    public UiActionIntent PrepareAction(UiActionCall request, string principal)
    {
        lock (_gate)
        {
            var entry = Get(request.Id, principal); var snapshot = entry.Snapshot; Revisions(snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var node = Flatten(snapshot.Roots).SingleOrDefault(n => n.Key == request.NodeKey);
            if (node?.ActionId == null || Disabled(node, snapshot.Roots)) throw new UiException("invalid_action", "No enabled action is exposed by this node.");
            var action = snapshot.Actions.Single(a => a.Id == node.ActionId);
            var text = action.Text == null ? null : ResolveString(action.Text, snapshot);
            var arguments = action.Arguments is { } args ? ResolveArguments(args, snapshot) : (JsonElement?)null;
            if (action.Kind == "openUrl" && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))) throw new UiException("invalid_url", "Only absolute HTTP(S) URLs without credentials are allowed.");
            return new(snapshot.Id, snapshot.Revision, action.Id, action.Kind, text, action.Tool, arguments);
        }
    }
    public UiSnapshot ChangeStateLocal(UiStateChange request) => ChangeState(request, LocalPrincipal(request.Id));
    public UiActionIntent PrepareActionLocal(UiActionCall request) => PrepareAction(request, LocalPrincipal(request.Id));
    public void Release(UiRelease request, string principal)
    {
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The surface changed.");
            _entries.Remove(request.Id); _generation = checked(_generation + 1);
        }
        NotifyReleased(request.Id);
    }
    public void ReleasePrincipal(string principal)
    {
        Principal(principal); string[] ids;
        lock (_gate)
        {
            ids = _entries.Where(pair => pair.Value.Principal == principal).Select(pair => pair.Key).ToArray();
            foreach (var id in ids) _entries.Remove(id);
            _generation = checked(_generation + 1); _lifetime = checked(_lifetime + 1);
        }
        foreach (var id in ids) NotifyReleased(id);
    }
    public void Clear()
    {
        string[] ids;
        lock (_gate) { ids = _entries.Keys.ToArray(); _entries.Clear(); _lifetime = checked(_lifetime + 1); _generation = checked(_generation + 1); }
        foreach (var id in ids) NotifyReleased(id);
    }
    private string LocalPrincipal(string id) { lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Principal : throw new UiException("unknown_surface", "The surface is no longer available."); }
    private Entry Get(string id, string principal)
    {
        Principal(principal);
        if (!_entries.TryGetValue(id, out var entry)) throw new UiException("unknown_surface", "The surface is no longer available.");
        Owner(entry, principal); return entry;
    }
    private static void Principal(string principal)
    { if (string.IsNullOrWhiteSpace(principal) || principal.Length > 200) throw new UiException("invalid_principal", "A transport-derived principal is required."); }
    private static void Owner(Entry entry, string principal)
    { if (entry.Principal != principal) throw new UiException("unknown_surface", "The surface is no longer available."); }
    private static void Revisions(UiSnapshot snapshot, long revision, long stateRevision)
    { if (snapshot.Revision != revision || snapshot.StateRevision != stateRevision) throw new UiException("revision_conflict", "The UI changed. Re-read it before interacting."); }
    private static bool SameStateType(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind || a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False;
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
            if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Length > Compiler.Limits.TextCharacters || field.Value.ValueKind == JsonValueKind.Number && !field.Value.TryGetDecimal(out _)) throw new UiException("invalid_state", "State slot exceeds its bounds.");
        }
        return state;
    }
    private void Notify(UiSnapshot snapshot)
    {
        if (Changed is { } handlers) foreach (Action<UiSnapshot> handler in handlers.GetInvocationList())
            try { handler(snapshot); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private void NotifyReleased(string id)
    {
        if (Released is { } handlers) foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(id); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private sealed record Entry(string Principal, UiTemplate Template, UiSnapshot Snapshot, string? Markdown);
}
