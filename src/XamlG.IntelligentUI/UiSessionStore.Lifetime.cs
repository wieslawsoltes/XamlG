namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
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
    private string LocalPrincipal(string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) ? entry.Principal
            : throw new UiException("unknown_surface", "The surface is no longer available.");
    }
    private Entry Get(string id, string principal)
    {
        Principal(principal);
        if (!_entries.TryGetValue(id, out var entry)) throw new UiException("unknown_surface", "The surface is no longer available.");
        Owner(entry, principal); return entry;
    }
    private static void Principal(string principal)
    {
        if (string.IsNullOrWhiteSpace(principal) || principal.Length > 200)
            throw new UiException("invalid_principal", "A transport-derived principal is required.");
    }
    private static void Owner(Entry entry, string principal)
    {
        if (entry.Principal != principal) throw new UiException("unknown_surface", "The surface is no longer available.");
    }
    private static void Revisions(UiSnapshot snapshot, long revision, long stateRevision)
    {
        if (snapshot.Revision != revision || snapshot.StateRevision != stateRevision)
            throw new UiException("revision_conflict", "The UI changed. Re-read it before interacting.");
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
