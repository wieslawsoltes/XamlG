using System.Runtime.CompilerServices;

namespace XamlG.CSharp.Resources;

/// <summary>Delayed exact memoization. A 1,024-bit monotone filter avoids an
/// identity-set entry for most one-off nodes; it NEVER authorizes a skip.</summary>
internal struct ResourceVisitFilter
{
    private ulong[]? _bits;
    private HashSet<object>? _repeated;

    internal int TrackedCount => _repeated?.Count ?? 0;

    public bool TryExpand(object node) => TryExpand(node, RuntimeHelpers.GetHashCode(node));

    // Fingerprints need not be unique. Keeping this operation explicit lets the
    // collision tests force every fingerprint to be identical. Exact equality
    // below is ALWAYS reference identity, never a bound record's recursive hash.
    internal bool TryExpand(object node, int fingerprint)
    {
        var bits = _bits ??= new ulong[16];
        var hash = unchecked((uint)fingerprint);
        var mixed = unchecked(hash * 0x9e3779b9U);
        mixed ^= mixed >> 16;
        var first = (int)(hash >> 6) & 15;
        var second = (int)(mixed >> 6) & 15;
        var firstBit = 1UL << (int)(hash & 63);
        var secondBit = 1UL << (int)(mixed & 63);
        var possibleRepeat = (bits[first] & firstBit) != 0 && (bits[second] & secondBit) != 0;
        bits[first] |= firstBit;
        bits[second] |= secondBit;
        if (!possibleRepeat) return true;
        return (_repeated ??= new(NodeIdentity.Instance)).Add(node);
    }

    // With stable fingerprints and bits that are never cleared: the first visit
    // either expands without an exact entry, or creates one after a collision.
    // The second visit must see both bits and creates/finds the exact entry.
    // Every later visit finds that entry and skips. Thus <= 2 expansions per node,
    // including when all fingerprints collide or the filter is fully saturated.
    // Do not evict exact entries or reset the filter during a scan.
    private sealed class NodeIdentity : IEqualityComparer<object>
    {
        public static readonly NodeIdentity Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
