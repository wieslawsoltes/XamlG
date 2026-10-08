using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
namespace XamlG.Roslyn;

public static class SymbolExtensions
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
    // Symbols are immutable. Weak keys keep repeated formatting off the binding/emission
    // hot path without retaining compilations across generator or language-server updates.
    private static readonly ConditionalWeakTable<ITypeSymbol, Name> CSharpNames = new();
    private static readonly ConditionalWeakTable<ISymbol, Name> MetadataNames = new();
    private static readonly ConditionalWeakTable<ITypeSymbol, MemberCache> MemberLists = new();
    private sealed class Name(string value) { public string Value { get; } = value; }
    public static string CSharpName(this ITypeSymbol type) => CSharpNames.GetValue(type, static type => new(type.ToDisplayString(TypeFormat))).Value;
    public static string MetadataName(this ISymbol symbol) => MetadataNames.GetValue(symbol, static symbol => new(FormatMetadataName(symbol))).Value;
    private static string FormatMetadataName(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type && type.ContainingType != null) return type.ContainingType.MetadataName() + "+" + type.MetadataName;
        var ns = symbol.ContainingNamespace; return ns == null || ns.IsGlobalNamespace ? symbol.MetadataName : ns.ToDisplayString() + "." + symbol.MetadataName;
    }
    public static bool HasMetadataName(this ISymbol symbol, string name)
    {
        if (name == null) return false;
        // Constructed named types keep their definition's metadata leaf name.
        // Reject unrelated types before Roslyn creates an OriginalDefinition
        // wrapper; framework binding performs many such negative probes.
        if (symbol is INamedTypeSymbol && !name.EndsWith(symbol.MetadataName, StringComparison.Ordinal)) return false;
        var definition = symbol.OriginalDefinition;
        return name.EndsWith(definition.MetadataName, StringComparison.Ordinal) &&
            string.Equals(definition.MetadataName(), name, StringComparison.Ordinal);
    }
    public static bool HasAttribute(this ISymbol symbol, IEnumerable<string> names)
    {
        foreach (var attribute in symbol.GetAttributes())
            if (attribute.AttributeClass is { } type)
                foreach (var name in names)
                    if (type.HasMetadataName(name)) return true;
        return false;
    }
    public static bool AcceptsNull(this ITypeSymbol type) => type.IsReferenceType || type.TypeKind == TypeKind.Dynamic || type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || type is ITypeParameterSymbol p && !p.HasValueTypeConstraint && !p.HasUnmanagedTypeConstraint;
    public static IEnumerable<ISymbol> Members(this ITypeSymbol type, string? name = null) =>
        MemberLists.GetValue(type, static type => new(type)).Get(name);

    private sealed class MemberCache
    {
        private readonly ITypeSymbol _type;
        private readonly ConcurrentDictionary<string, ImmutableArray<ISymbol>> _named = new(StringComparer.Ordinal);
        private readonly Lazy<ImmutableArray<ISymbol>> _all;
        public MemberCache(ITypeSymbol type)
        {
            _type = type;
            _all = new(() => EnumerateMembers(type, null).ToImmutableArray());
        }
        public ImmutableArray<ISymbol> Get(string? name) => name == null ? _all.Value :
            _named.TryGetValue(name, out var members) ? members : _named.GetOrAdd(name, Read);
        private ImmutableArray<ISymbol> Read(string name) => EnumerateMembers(_type, name).ToImmutableArray();
    }

    private static IEnumerable<ISymbol> EnumerateMembers(ITypeSymbol type, string? name)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            foreach (var member in name == null ? current.GetMembers() : current.GetMembers(name)) yield return member;
        if (type.TypeKind == TypeKind.Interface)
            foreach (var contract in type.AllInterfaces)
                foreach (var member in name == null ? contract.GetMembers() : contract.GetMembers(name)) yield return member;
    }
}
