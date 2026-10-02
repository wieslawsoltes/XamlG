using Microsoft.CodeAnalysis;
namespace XamlG.Roslyn;

public static class SymbolExtensions
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
    public static string CSharpName(this ITypeSymbol type) => type.ToDisplayString(TypeFormat);
    public static string MetadataName(this ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type && type.ContainingType != null) return type.ContainingType.MetadataName() + "+" + type.MetadataName;
        var ns = symbol.ContainingNamespace; return ns == null || ns.IsGlobalNamespace ? symbol.MetadataName : ns.ToDisplayString() + "." + symbol.MetadataName;
    }
    public static bool HasMetadataName(this ISymbol symbol, string name) => string.Equals(symbol.OriginalDefinition.MetadataName(), name, StringComparison.Ordinal);
    public static bool HasAttribute(this ISymbol symbol, IEnumerable<string> names) => symbol.GetAttributes().Any(a => a.AttributeClass != null && names.Contains(a.AttributeClass.MetadataName(), StringComparer.Ordinal));
    public static bool AcceptsNull(this ITypeSymbol type) => type.IsReferenceType || type.TypeKind == TypeKind.Dynamic || type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || type is ITypeParameterSymbol p && !p.HasValueTypeConstraint && !p.HasUnmanagedTypeConstraint;
    public static IEnumerable<ISymbol> Members(this ITypeSymbol type, string? name = null)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            foreach (var member in name == null ? current.GetMembers() : current.GetMembers(name)) yield return member;
        if (type.TypeKind == TypeKind.Interface)
            foreach (var contract in type.AllInterfaces)
                foreach (var member in name == null ? contract.GetMembers() : contract.GetMembers(name)) yield return member;
    }
}
