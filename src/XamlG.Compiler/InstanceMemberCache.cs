using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;

namespace XamlG.Compiler;

/// <summary>Memoizes only immutable property/event selection, never occurrence
/// locations, bound members, custom rules, conversions or attached overloads.</summary>
internal sealed class InstanceMemberCache(RoslynTypeSystem types)
{
    private const int MaximumEntries = 4096;
    private const int MaximumNameLength = 256;
    private static readonly ConditionalWeakTable<RoslynTypeSystem, InstanceMemberCache> Caches = new();
    private readonly ConcurrentDictionary<Key, Selection> _entries = new(KeyComparer.Instance);
    private int _count;

    public static InstanceMemberCache For(RoslynTypeSystem types) =>
        Caches.GetValue(types, static value => new(value));

    public ISymbol? Resolve(ITypeSymbol target, string name, INamedTypeSymbol? within, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var retain = name.Length <= MaximumNameLength;
        var key = new Key(target, name, within);
        if (retain && _entries.TryGetValue(key, out var cached)) return cached.Symbol;
        var symbol = Find(target, name, within, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!retain) return symbol;
        // Reserve bounded capacity without Count's global lock. A race for the
        // same key may compute twice, but cannot publish a different selection.
        var count = Volatile.Read(ref _count);
        while (count < MaximumEntries)
        {
            var observed = Interlocked.CompareExchange(ref _count, count + 1, count);
            if (observed != count) { count = observed; continue; }
            if (!_entries.TryAdd(key, new(symbol))) Interlocked.Decrement(ref _count);
            break;
        }
        return symbol;
    }

    private ISymbol? Find(ITypeSymbol target, string name, INamedTypeSymbol? within, CancellationToken cancellation)
    {
        // Preserve Members' class/base/interface ordering, and search ALL
        // properties before ANY event. A derived event does not mask the base
        // property under the existing XAML member-selection contract.
        for (var kind = 0; kind < 2; kind++)
        {
            for (var current = target as INamedTypeSymbol; current != null; current = current.BaseType)
            {
                cancellation.ThrowIfCancellationRequested();
                if (FindDeclared(current, name, within, kind == 0) is { } member) return member;
            }
            if (target.TypeKind == TypeKind.Interface)
                foreach (var contract in target.AllInterfaces)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (FindDeclared(contract, name, within, kind == 0) is { } member) return member;
                }
        }
        return null;
    }

    private ISymbol? FindDeclared(INamedTypeSymbol target, string name, INamedTypeSymbol? within, bool property)
    {
        foreach (var member in target.GetMembers(name))
        {
            var eligible = property
                ? member is IPropertySymbol { IsStatic: false, IsIndexer: false }
                : member is IEventSymbol { IsStatic: false };
            if (eligible && types.IsAccessible(member, within)) return member;
        }
        return null;
    }

    private readonly record struct Selection(ISymbol? Symbol);
    // Do not merge distinct constructed symbol instances: Roslyn's default
    // equality ignores nullable annotations and tuple element names that custom
    // binding rules can observe on the selected property's value type.
    private readonly record struct Key(ITypeSymbol Target, string Name, INamedTypeSymbol? Within);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals(Key left, Key right) => left.Name == right.Name &&
            ReferenceEquals(left.Target, right.Target) &&
            ReferenceEquals(left.Within, right.Within);
        public int GetHashCode(Key key) => unchecked((RuntimeHelpers.GetHashCode(key.Target) * 397 ^
            StringComparer.Ordinal.GetHashCode(key.Name)) * 397 ^
            (key.Within == null ? 0 : RuntimeHelpers.GetHashCode(key.Within)));
    }
}
