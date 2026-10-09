using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XamlG.IntelligentUI;

/// <summary>Bounded, owner-scoped sessions. Failed compilations/evaluations and stale writes never publish partial state.</summary>
public sealed class UiSessionStore(UiCompiler? compiler = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public UiCompiler Compiler { get; } = compiler ?? new();
    public event Action<UiSnapshot>? Changed;
    public event Action<string>? Released;
    public UiSnapshot Publish(UiPublish request, string principal)
    {
        Principal(principal); UiJson.Identifier(request.Id, "Surface ID");
        if (request.ExpectedRevision < 0 || request.Sequence < 1) throw new UiException("revision_conflict", "Invalid revision or stream sequence.");
        var compiled = Compiler.Compile(request.Xaml, request.IsFinal);
        if (!compiled.Success) throw new UiException("compilation_failed", string.Join("\n", compiled.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var actions = ValidateActions(request.Actions ?? []);
        if (request.FallbackMarkdown?.Length > Compiler.Limits.TextCharacters) throw new UiException("text_limit", "Fallback exceeds the text limit.");
        UiSnapshot snapshot;
        lock (_gate)
        {
            _entries.TryGetValue(request.Id, out var current);
            if (current != null) Owner(current, principal);
            if ((current?.Snapshot.Revision ?? 0) != request.ExpectedRevision || request.Sequence != (current?.Snapshot.Sequence ?? 0) + 1)
                throw new UiException("revision_conflict", "Re-read the surface revision and next stream sequence.");
            if (current == null && _entries.Count >= Compiler.Limits.Surfaces) throw new UiException("surface_limit", "Release an existing surface before creating another.");
            var state = MergeState(current?.Snapshot.State, request.InitialState);
            var data = UiJson.Object(request.Data ?? current?.Snapshot.Data, Compiler.Limits.DataBytes, "Data");
            var roots = compiled.Template!.Render(state, data);
            ValidateActionReferences(roots, actions);
            snapshot = new(request.Id, checked(request.ExpectedRevision + 1), current?.Snapshot.StateRevision ?? 0,
                request.Sequence, request.IsFinal, request.Xaml, state, data, roots, actions, compiled.Diagnostics,
                Fallback(roots, request.FallbackMarkdown));
            if (current != null && !JsonElement.DeepEquals(current.Snapshot.State, state)) snapshot = snapshot with { StateRevision = checked(snapshot.StateRevision + 1) };
            _entries[request.Id] = new(principal, compiled.Template, snapshot, request.FallbackMarkdown);
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot Read(string id, string principal) { lock (_gate) return Get(id, principal).Snapshot; }
    /// <summary>Trusted local presentation only. Never expose this owner-bypassing path to an agent or transport.</summary>
    public UiSnapshot? ReadLocal(string id) { lock (_gate) return _entries.GetValueOrDefault(id)?.Snapshot; }
    public UiSnapshot ChangeState(UiStateChange request, string principal)
    {
        UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal); Revisions(entry.Snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            if (!entry.Snapshot.State.TryGetProperty(request.Key, out var old)) throw new UiException("unknown_state", "Unknown state key.");
            if (old.ValueKind != request.Value.ValueKind && !(old.ValueKind is JsonValueKind.True or JsonValueKind.False && request.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                throw new UiException("invalid_state", "Input state cannot change its declared type.");
            var inputs = Flatten(entry.Snapshot.Roots).Where(node => node.StateKey == request.Key).ToArray();
            if (inputs.Length == 0 || inputs.All(node => Disabled(node, entry.Snapshot.Roots))) throw new UiException("invalid_state", "No enabled input exposes this state key.");
            var model = JsonNode.Parse(entry.Snapshot.State.GetRawText())!.AsObject();
            model[request.Key] = JsonNode.Parse(request.Value.GetRawText());
            var state = ValidateState(JsonSerializer.SerializeToElement(model));
            var roots = entry.Template.Render(state, entry.Snapshot.Data);
            snapshot = entry.Snapshot with { State = state, StateRevision = checked(entry.Snapshot.StateRevision + 1), Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot };
        }
        Notify(snapshot); return snapshot;
    }
    public UiSnapshot ChangeData(UiDataChange request, string principal)
    {
        var data = UiJson.Object(request.Data, Compiler.Limits.DataBytes, "Data");
        UiSnapshot snapshot;
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The surface changed before data arrived.");
            var roots = entry.Template.Render(entry.Snapshot.State, data);
            snapshot = entry.Snapshot with { Revision = checked(entry.Snapshot.Revision + 1), Data = data, Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot };
        }
        Notify(snapshot); return snapshot;
    }
    public UiActionIntent PrepareAction(UiActionCall request, string principal)
    {
        lock (_gate)
        {
            var entry = Get(request.Id, principal); var snapshot = entry.Snapshot;
            Revisions(snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var node = Flatten(snapshot.Roots).SingleOrDefault(n => n.Key == request.NodeKey);
            if (node?.ActionId == null || Disabled(node, snapshot.Roots)) throw new UiException("invalid_action", "No enabled action is exposed by this node.");
            var action = snapshot.Actions.Single(a => a.Id == node.ActionId);
            var text = action.Text == null ? null : ResolveString(action.Text, snapshot);
            var arguments = action.Arguments is { } args ? ResolveArguments(args, snapshot) : (JsonElement?)null;
            if (action.Kind == "openUrl" && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new UiException("invalid_url", "Only absolute HTTP(S) URLs without credentials are allowed.");
            return new(snapshot.Id, snapshot.Revision, action.Id, action.Kind, text, action.Tool, arguments);
        }
    }
    /// <summary>Trusted local UI interaction. Remote callers must use the principal-checked method.</summary>
    public UiSnapshot ChangeStateLocal(UiStateChange request) => ChangeState(request, LocalPrincipal(request.Id));
    public UiActionIntent PrepareActionLocal(UiActionCall request) => PrepareAction(request, LocalPrincipal(request.Id));
    public void Release(UiRelease request, string principal)
    {
        lock (_gate)
        {
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The surface changed.");
            _entries.Remove(request.Id);
        }
        NotifyReleased(request.Id);
    }
    public void Clear()
    {
        string[] ids;
        lock (_gate) { ids = _entries.Keys.ToArray(); _entries.Clear(); }
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
    private JsonElement MergeState(JsonElement? old, JsonElement? initial)
    {
        if (initial == null) return ValidateState(old ?? UiJson.Empty);
        var defaults = ValidateState(initial.Value);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var value in defaults.EnumerateObject())
        {
            var previous = old is { } oldState && oldState.TryGetProperty(value.Name, out var found) ? found : value.Value;
            var sameType = previous.ValueKind == value.Value.ValueKind || previous.ValueKind is JsonValueKind.True or JsonValueKind.False && value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            result.Add(value.Name, sameType ? previous : value.Value);
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
            if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new UiException("invalid_state", "State slots must be string, number or boolean values.");
            if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Length > Compiler.Limits.TextCharacters || field.Value.ValueKind == JsonValueKind.Number && !field.Value.TryGetDecimal(out _))
                throw new UiException("invalid_state", "State slot exceeds its bounds.");
        }
        return state;
    }
    private ImmutableArray<UiAction> ValidateActions(UiAction[] actions)
    {
        if (actions.Length > Compiler.Limits.Actions) throw new UiException("action_limit", "Too many actions.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<UiAction>();
        foreach (var action in actions)
        {
            if (action == null) throw new UiException("invalid_action", "Null action.");
            UiJson.Identifier(action.Id, "Action ID");
            if (!ids.Add(action.Id) || action.Kind is not ("message" or "copy" or "openUrl" or "tool")) throw new UiException("invalid_action", "Invalid or duplicate action.");
            if (action.Kind == "tool")
            {
                if (action.Tool == null || action.Tool.Length > 64 || !action.Tool.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw new UiException("invalid_action", "Tool name is invalid.");
                if (action.Text != null) throw new UiException("invalid_action", "Tool actions do not accept Text.");
            }
            else if (action.Text == null || action.Tool != null || action.Arguments != null) throw new UiException("invalid_action", "This action requires only Text.");
            if (action.Text != null) ValidateString(action.Text);
            JsonElement? arguments = action.Arguments is { } args ? UiJson.Object(args, Compiler.Limits.DataBytes, "Action arguments") : null;
            if (arguments is { } argumentObject) ValidateArgumentExpressions(argumentObject);
            result.Add(action with { Arguments = arguments });
        }
        return result.ToImmutable();
    }
    private void ValidateArgumentExpressions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) ValidateString(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Object) foreach (var field in value.EnumerateObject()) ValidateArgumentExpressions(field.Value);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var field in value.EnumerateArray()) ValidateArgumentExpressions(field);
    }
    private void ValidateString(string value)
    {
        if (value.Length > Compiler.Limits.TextCharacters) throw new UiException("text_limit", "Action text exceeds its limit.");
        if (value.StartsWith("{ui:Expr ", StringComparison.Ordinal)) ParseExpression(value);
    }
    private UiExpression ParseExpression(string value)
    {
        if (!value.EndsWith('}')) throw new UiException("invalid_expression", "Unterminated action expression.");
        return UiExpression.Parse(value[9..^1], Compiler.Limits);
    }
    private string ResolveString(string value, UiSnapshot snapshot) => value.StartsWith("{ui:Expr ", StringComparison.Ordinal)
        ? UiJson.Text(UiJson.Value(ParseExpression(value).Evaluate(snapshot.State, snapshot.Data))) : value;
    private JsonElement ResolveArguments(JsonElement value, UiSnapshot snapshot)
    {
        object? Visit(JsonElement item) => item.ValueKind switch
        {
            JsonValueKind.String when item.GetString()!.StartsWith("{ui:Expr ", StringComparison.Ordinal) => ParseExpression(item.GetString()!).Evaluate(snapshot.State, snapshot.Data),
            JsonValueKind.Object => item.EnumerateObject().ToDictionary(p => p.Name, p => Visit(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => item.EnumerateArray().Select(Visit).ToArray(), _ => item
        };
        return UiJson.Object(JsonSerializer.SerializeToElement(Visit(value)), Compiler.Limits.DataBytes, "Resolved action arguments");
    }
    private static void ValidateActionReferences(ImmutableArray<UiElement> roots, ImmutableArray<UiAction> actions)
    {
        var ids = actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (Flatten(roots).Any(node => node.ActionId != null && !ids.Contains(node.ActionId))) throw new UiException("invalid_action", "A Button references an undeclared action.");
    }
    public static IEnumerable<UiElement> Flatten(IEnumerable<UiElement> nodes)
    { foreach (var node in nodes) { yield return node; foreach (var child in Flatten(node.Children)) yield return child; } }
    private static bool Disabled(UiElement target, IEnumerable<UiElement> roots)
    {
        foreach (var node in roots)
        {
            if (node.Key == target.Key) return IsDisabled(node);
            if (Flatten(node.Children).Any(child => child.Key == target.Key)) return IsDisabled(node) || Disabled(target, node.Children);
        }
        return true;
        static bool IsDisabled(UiElement node) => node.Properties.TryGetValue("IsEnabled", out var enabled) && !enabled.GetBoolean() || node.Properties.TryGetValue("IsVisible", out var visible) && !visible.GetBoolean();
    }
    private string Fallback(ImmutableArray<UiElement> roots, string? markdown)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(markdown)) lines.Add(markdown);
        foreach (var node in Flatten(roots))
        {
            if (DisabledVisibility(node, roots)) continue;
            foreach (var field in node.Properties.Where(p => p.Key is "Text" or "Content" or "Value" or "IsChecked"))
                lines.Add(UiJson.Text(UiJson.Value(field.Value)));
        }
        var result = string.Join("\n\n", lines);
        return result.Length <= Compiler.Limits.TextCharacters ? result : result[..Compiler.Limits.TextCharacters] + "\n[UI fallback shortened]";
        static bool DisabledVisibility(UiElement target, IEnumerable<UiElement> nodes)
        {
            foreach (var node in nodes)
                if (node.Key == target.Key || Flatten(node.Children).Any(n => n.Key == target.Key))
                    return node.Properties.TryGetValue("IsVisible", out var visible) && !visible.GetBoolean() || node.Key != target.Key && DisabledVisibility(target, node.Children);
            return false;
        }
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
