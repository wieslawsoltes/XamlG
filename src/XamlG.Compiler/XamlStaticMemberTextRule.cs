using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Resolves named static values on profile-selected types without loading
/// application assemblies or evaluating getters during compilation.</summary>
public sealed class XamlStaticMemberTextRule : IXamlTextConversionRule
{
    private readonly ImmutableArray<string> _typeMetadataNames;

    public XamlStaticMemberTextRule(IEnumerable<string> typeMetadataNames)
    {
        if (typeMetadataNames == null) throw new ArgumentNullException(nameof(typeMetadataNames));
        _typeMetadataNames = typeMetadataNames.Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (_typeMetadataNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Type metadata identities must not be empty.", nameof(typeMetadataNames));
    }

    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType,
        NamespaceScope scope, TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol namedType ||
            !_typeMetadataNames.Any(namedType.OriginalDefinition.HasMetadataName)) return false;
        context.Cancellation.ThrowIfCancellationRequested();
        var name = text.Trim();
        if (name.Length == 0) return true;
        ISymbol? selected = null;
        ITypeSymbol? selectedType = null;
        foreach (var candidate in namedType.GetMembers(name))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (!context.Types.IsAccessible(candidate)) continue;
            var valueType = candidate switch
            {
                IPropertySymbol { IsStatic: true, IsIndexer: false, GetMethod: { } getter } property
                    when context.Types.IsAccessible(getter) => property.Type,
                IFieldSymbol { IsStatic: true } field when field.IsReadOnly || field.HasConstantValue => field.Type,
                _ => null
            };
            if (valueType == null || !SymbolEqualityComparer.Default.Equals(valueType, targetType)) continue;
            // An ambiguous metadata surface is a conversion failure, not an arbitrary choice.
            if (selected != null) return true;
            selected = candidate;
            selectedType = valueType;
        }
        if (selected != null)
        {
            context.Symbols.Add(new(span, selected, "static"));
            expression = new BoundStaticExpression(selected, selectedType!, span);
        }
        // TryText participates in speculative overload ranking. Let the committed
        // BindText operation diagnose failure instead of leaking speculative errors.
        return true;
    }
}
