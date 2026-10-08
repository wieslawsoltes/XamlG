using System.Security.Cryptography;

namespace XamlG.Automation;

public sealed record AutomationArtifact(string Id, string Name, string MimeType, int Length, string Sha256, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record AutomationArtifactChunk(AutomationArtifact Artifact, int Offset, int Count, string Base64, bool HasMore);

/// <summary>Immutable in-memory build snapshots. Embedders supply transport-owned
/// principal identities and clear the store when its workspace/session is revoked.</summary>
public sealed class AutomationArtifactStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private bool _disposed;
    public AutomationArtifactStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Expire(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }
    public event Action<string>? Changed;
    public IReadOnlyList<AutomationArtifact> LocalInventory { get { lock (_gate) { ExpireCore(); return _entries.Values.Select(entry => entry.Metadata).ToArray(); } } }
    public AutomationArtifact Add(string principalId, string name, string mimeType, long revision, ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(principalId) || principalId.Length > 512 || name.Length > 256 || mimeType.Length > 128 || bytes.Length > 32 * 1024 * 1024)
            throw new ArgumentException("Artifact metadata or content exceeds its bounds.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ExpireCore();
            while (_entries.Count >= 8 || _entries.Values.Sum(entry => (long)entry.Metadata.Length) + bytes.Length > 32 * 1024 * 1024)
                Remove(_entries.Values.MinBy(entry => entry.Metadata.CreatedAt)!.Metadata.Id);
            var snapshot = bytes.ToArray();
            var now = _time.GetUtcNow();
            var metadata = new AutomationArtifact(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), name, mimeType, bytes.Length,
                Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant(), revision, now, now.AddMinutes(5));
            _entries.Add(metadata.Id, new(metadata, principalId, snapshot)); Notify(metadata.Id); return metadata;
        }
    }
    public AutomationArtifactChunk Read(string id, string principalId, int offset = 0, int count = 262144)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); ExpireCore();
            if (!_entries.TryGetValue(id, out var entry) || entry.PrincipalId != principalId) throw Missing();
            if (offset < 0 || offset > entry.Metadata.Length || count is < 1 or > 262144) throw new ArgumentOutOfRangeException(nameof(offset));
            var length = Math.Min(count, entry.Metadata.Length - offset);
            return new(entry.Metadata, offset, length, Convert.ToBase64String(entry.Bytes, offset, length), offset + length < entry.Metadata.Length);
        }
    }
    public void Release(string id, string principalId)
    {
        lock (_gate)
        {
            ExpireCore();
            if (!_entries.TryGetValue(id, out var entry) || entry.PrincipalId != principalId) throw Missing();
            Remove(id);
        }
    }
    /// <summary>Owner-only local UI control; do not expose this inventory/release operation to remote clients.</summary>
    public void ReleaseLocal(string id) { lock (_gate) Remove(id); }
    public byte[] ReadLocal(string id)
    {
        lock (_gate) { ExpireCore(); return _entries.TryGetValue(id, out var entry) ? entry.Bytes.ToArray() : throw Missing(); }
    }
    /// <summary>Retire transport-owned artifacts while retaining independent local sessions.</summary>
    public void ClearExceptPrincipals(IReadOnlyCollection<string> retainedPrincipals)
    {
        ArgumentNullException.ThrowIfNull(retainedPrincipals);
        lock (_gate) foreach (var id in _entries.Where(pair => !retainedPrincipals.Contains(pair.Value.PrincipalId)).Select(pair => pair.Key).ToArray()) Remove(id);
    }
    public void Clear() { lock (_gate) foreach (var id in _entries.Keys.ToArray()) Remove(id); }
    private void Expire() { lock (_gate) if (!_disposed) ExpireCore(); }
    private void ExpireCore() { foreach (var id in _entries.Values.Where(entry => entry.Metadata.ExpiresAt <= _time.GetUtcNow()).Select(entry => entry.Metadata.Id).ToArray()) Remove(id); }
    private void Remove(string id) { if (_entries.Remove(id)) Notify(id); }
    private void Notify(string id)
    {
        if (Changed is not { } observers) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(id); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private static AutomationException Missing() => new("unknown_artifact", "The artifact is unavailable, expired or belongs to another principal.");
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _timer.Dispose(); _entries.Clear(); Changed = null; } }
    private sealed record Entry(AutomationArtifact Metadata, string PrincipalId, byte[] Bytes);
}
