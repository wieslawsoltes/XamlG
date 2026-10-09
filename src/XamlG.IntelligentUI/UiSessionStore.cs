using System.Text.Json;

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
            if ((current?.Snapshot.Revision ?? 0) != request.ExpectedRevision || request.Sequence != (current?.Snapshot.Sequence ?? 0) + 1)
                throw new UiException("revision_conflict", "Re-read the surface revision and next stream sequence.");
            if (current == null && _entries.Count >= Compiler.Limits.Surfaces) throw new UiException("surface_limit", "Release an existing surface before creating another.");
            var state = MergeState(current?.Snapshot.State, request.InitialState);
            var data = UiJson.Object(request.Data ?? current?.Snapshot.Data, Compiler.Limits.DataBytes, "Data");
            var roots = compiled.Template!.Render(state, data, current == null ? null : current.Snapshot.Roots);
            ValidateActionReferences(roots, actions);
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
        Principal(principal);
        lock (_gate) return _entries.Values.Where(entry => entry.Principal == principal).OrderBy(entry => entry.Snapshot.Id, StringComparer.Ordinal).Select(entry => UiPresentation.From(entry.Snapshot)).ToArray();
    }
    /// <summary>Trusted local presentation only. Never expose this method to agents or transports.</summary>
    public UiSnapshot? ReadLocal(string id) { lock (_gate) return _entries.GetValueOrDefault(id)?.Snapshot; }
    public IReadOnlyList<UiSnapshot> ListLocal() { lock (_gate) return _entries.Values.Select(entry => entry.Snapshot).ToArray(); }
}
