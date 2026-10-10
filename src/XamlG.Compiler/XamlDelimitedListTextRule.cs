using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
using XamlG.Internal;

namespace XamlG.Compiler;

/// <summary>A profile-selected, symbol-only conversion for comma/whitespace-delimited lists.
/// Collection identities are supplied by the framework; element literals use the normal
/// conversion pipeline, and emitted arrays or constructors preserve the target contract.</summary>
public sealed class XamlDelimitedListTextRule : IXamlTextConversionRule
{
    private static readonly char[] Separators = { ',', ' ', '\t', '\r', '\n' };
    private readonly ImmutableArray<string> _definitions;

    public XamlDelimitedListTextRule(IEnumerable<string> collectionMetadataNames)
    {
        if (collectionMetadataNames == null) throw new ArgumentNullException(nameof(collectionMetadataNames));
        _definitions = collectionMetadataNames.Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (_definitions.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Collection metadata identities must not be empty.", nameof(collectionMetadataNames));
    }

    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol { TypeArguments.Length: 1 } collection ||
            !_definitions.Any(name => collection.OriginalDefinition.HasMetadataName(name))) return false;
        var element = collection.TypeArguments[0];
        var values = ImmutableArray.CreateBuilder<BoundExpression>();
        foreach (var token in new SpanSplitEnumerator(text.AsSpan(), Separators, removeEmpty: true))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var value = context.Values.TryText(token.ToString(), element, scope, span);
            if (value == null) return true;
            values.Add(value);
        }
        var arrayType = context.Types.Compilation.CreateArrayTypeSymbol(element);
        var array = new BoundArrayExpression(values.ToImmutable(), arrayType, span);
        if (context.Types.Compilation.ClassifyCommonConversion(arrayType, targetType).IsImplicit)
        { expression = array; return true; }
        var constructors = collection.InstanceConstructors.Where(method => method.Parameters.Length == 1 &&
            method.Parameters[0].RefKind == RefKind.None && context.Types.IsAccessible(method) &&
            context.Types.Compilation.ClassifyCommonConversion(arrayType, method.Parameters[0].Type).IsImplicit).ToArray();
        // Do not pick an arbitrary overload; an exact array constructor wins, otherwise
        // only an unambiguous collection constructor is accepted.
        var exact = constructors.Where(method => SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, arrayType)).ToArray();
        var selected = exact.Length == 1 ? exact[0] : constructors.Length == 1 ? constructors[0] : null;
        if (selected != null) expression = new BoundNewExpression(selected, ImmutableArray.Create<BoundExpression>(array), span);
        return true;
    }
}
