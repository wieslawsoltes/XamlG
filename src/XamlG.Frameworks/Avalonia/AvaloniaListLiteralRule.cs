using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Resolves framework list literals, including inherited separator metadata and point pairs.</summary>
public sealed class AvaloniaListLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        var element = targetType is IArrayTypeSymbol array ? array.ElementType : targetType.AllInterfaces
            .FirstOrDefault(type => type.OriginalDefinition.HasMetadataName(ClrNames.IEnumerableOfT))?.TypeArguments[0];
        if (element == null) return false;
        var named = targetType as INamedTypeSymbol;
        var definition = named?.OriginalDefinition;
        var asArray = targetType is IArrayTypeSymbol { Rank: 1 } || definition?.HasMetadataName("System.Collections.Generic.IList`1") == true ||
            definition?.HasMetadataName(AvaloniaRegisteredSetterMetadata.ReadOnlyList) == true;
        var list = context.Types.Find(AvaloniaRegisteredSetterMetadata.List)?.Construct(element);
        if (!asArray && (list == null || !context.Types.Compilation.ClassifyCommonConversion(targetType, list).IsImplicit)) return false;
        string[] items;
        if (element.HasMetadataName("Avalonia.Point"))
        {
            var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length % 2 != 0) return Invalid(context, "A point list requires coordinate pairs.", span);
            items = Enumerable.Range(0, parts.Length / 2).Select(index => parts[index * 2] + " " + parts[index * 2 + 1]).ToArray();
        }
        else
        {
            string[]? separators = new[] { "," };
            var options = 3;
            AttributeData? attribute = null;
            for (var current = named; current != null && attribute == null; current = current.BaseType)
                attribute = current.GetAttributes().FirstOrDefault(value => value.AttributeClass?.HasMetadataName("Avalonia.Metadata.AvaloniaListAttribute") == true);
            if (attribute != null)
                foreach (var argument in attribute.NamedArguments)
                {
                    if (argument.Key == "Separators") separators = argument.Value.IsNull ? null : argument.Value.Values.Where(value => value.Value is string).Select(value => (string)value.Value!).ToArray();
                    if (argument.Key == "SplitOptions" && argument.Value.Value is int flags) options = flags;
                }
            try
            {
                // Preserve the pinned netstandard transform's XOR and separate trimming, including whitespace-only entries.
                items = text.Split(separators, (StringSplitOptions)(options ^ 2));
                if ((options & 2) != 0) items = items.Select(value => value.Trim()).ToArray();
            }
            catch (ArgumentException) { return Invalid(context, "The list split options are invalid.", span); }
        }
        var values = ImmutableArray.CreateBuilder<BoundExpression>(items.Length);
        foreach (var item in items)
        {
            var value = context.Values.TryText(item, element, scope, span);
            if (value == null) return false;
            if (value.Type == null || !context.Types.Compilation.ClassifyCommonConversion(value.Type, element).IsImplicit)
                return Invalid(context, $"List item '{value.Type}' cannot be assigned to '{element}'.", span);
            values.Add(Suppress(value));
        }
        if (asArray) expression = new BoundArrayExpression(values.ToImmutable(), context.Types.Compilation.CreateArrayTypeSymbol(element), span);
        else
        {
            var constructor = named!.InstanceConstructors.FirstOrDefault(method => method.Parameters.IsEmpty && context.Types.IsAccessible(method));
            var add = named.Members(ClrNames.Add).OfType<IMethodSymbol>().FirstOrDefault(method => !method.IsStatic && method.ReturnsVoid &&
                method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None && context.Types.IsAccessible(method) &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, element));
            var capacity = named.Members("Capacity").OfType<IPropertySymbol>().FirstOrDefault(property => !property.IsStatic &&
                property.Type.SpecialType == SpecialType.System_Int32 && property.SetMethod != null && context.Types.IsAccessible(property.SetMethod));
            if (constructor == null || add == null || capacity == null)
                return Invalid(context, $"List literal '{targetType}' requires an accessible default constructor, Add and Capacity setter.", span);
            expression = new BoundCollectionExpression(constructor, add, capacity, values.ToImmutable(), span);
        }
        expression = expression with { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        return true;
    }

    private static BoundExpression Suppress(BoundExpression value) => value is BoundCastExpression cast
        ? cast with { Value = Suppress(cast.Value), SuppressSourceInfo = true } : value with { SuppressSourceInfo = true };

    private static bool Invalid(BindingContext context, string message, TextSpan span)
    { context.Report("XG3004", message, span); return true; }
}
