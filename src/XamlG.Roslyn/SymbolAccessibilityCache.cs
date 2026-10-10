using System.Collections.Concurrent;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace XamlG.Roslyn;

/// <summary>Bounded memoization owned by one immutable compilation. Accessibility
/// depends on the containing access context, including negative/private results.</summary>
internal sealed class SymbolAccessibilityCache(CSharpCompilation compilation)
{
    private const int MaximumEntries = 16384;
    private readonly ConcurrentDictionary<Key, bool> _entries = new(KeyComparer.Instance);
    private int _count;

    public bool IsAccessible(ISymbol symbol, INamedTypeSymbol? within)
    {
        if (symbol == null) throw new ArgumentNullException(nameof(symbol));
        var key = new Key(symbol, within);
        if (_entries.TryGetValue(key, out var cached)) return cached;
        var result = compilation.IsSymbolAccessibleWithin(symbol, (ISymbol?)within ?? compilation.Assembly);
        // Reserve bounded capacity without ConcurrentDictionary.Count's global
        // lock or an overflowing miss counter. Saturation affects reuse, not results.
        var count = Volatile.Read(ref _count);
        while (count < MaximumEntries)
        {
            var observed = Interlocked.CompareExchange(ref _count, count + 1, count);
            if (observed != count) { count = observed; continue; }
            if (!_entries.TryAdd(key, result)) Interlocked.Decrement(ref _count);
            break;
        }
        return result;
    }

    private readonly record struct Key(ISymbol Symbol, INamedTypeSymbol? Within);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals(Key x, Key y) => SymbolEqualityComparer.Default.Equals(x.Symbol, y.Symbol) &&
            SymbolEqualityComparer.Default.Equals(x.Within, y.Within);
        public int GetHashCode(Key key) => unchecked(SymbolEqualityComparer.Default.GetHashCode(key.Symbol) * 397 ^
            (key.Within == null ? 0 : SymbolEqualityComparer.Default.GetHashCode(key.Within)));
    }
}
