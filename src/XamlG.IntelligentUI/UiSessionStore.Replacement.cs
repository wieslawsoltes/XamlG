namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    /// <summary>Trusted owner UI only. Never expose this cross-principal inventory as an MCP tool.</summary>
    public IReadOnlyList<UiSnapshot> SnapshotLocal()
    { lock (_gate) return _entries.Values.Select(entry => entry.Snapshot).OrderBy(snapshot => snapshot.Id, StringComparer.Ordinal).ToArray(); }

    /// <summary>Validate a private archive before atomically replacing an existing workspace.
    /// Concurrent edits reject the replacement. Live replacements receive fresh revisions so an
    /// old review cannot become valid again; preserved session IDs still reconnect transcript cards.</summary>
    public void ReplaceArchive(string json, string workspaceId, long expectedGeneration)
    {
        var candidate = new UiSessionStore(Compiler);
        candidate.RestoreArchive(json, workspaceId);
        string[] released; UiSnapshot[] snapshots;
        lock (_gate)
        {
            if (_generation != expectedGeneration) throw new UiException("revision_conflict", "The live UI changed while loading the archive.");
            var revision = Math.Max(_revision, candidate._revision);
            var replacements = candidate._entries.ToDictionary(pair => pair.Key, pair => pair.Value with
            { Snapshot = pair.Value.Snapshot with { Revision = checked(++revision), StateRevision = checked(pair.Value.Snapshot.StateRevision + 1) } }, StringComparer.Ordinal);
            released = _entries.Where(pair => !replacements.TryGetValue(pair.Key, out var next) || pair.Value.Snapshot.SessionId != next.Snapshot.SessionId).Select(pair => pair.Key).ToArray();
            var generation = checked(_generation + 1); var lifetime = checked(_lifetime + 1);
            _entries.Clear(); foreach (var pair in replacements) _entries.Add(pair.Key, pair.Value);
            _revision = revision; _generation = generation; _lifetime = lifetime;
            snapshots = replacements.Values.Select(entry => entry.Snapshot).ToArray();
        }
        foreach (var id in released) NotifyReleased(id);
        foreach (var snapshot in snapshots) Notify(snapshot);
    }
}
