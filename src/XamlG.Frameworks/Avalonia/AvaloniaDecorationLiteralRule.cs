using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Lowers the decoration parser fallback after the static and list intrinsics have had priority.</summary>
public sealed class AvaloniaDecorationLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (!targetType.HasMetadataName("Avalonia.Media.TextDecorationCollection")) return false;
        try
        {
            var parsed = Parsing.TextDecorationCollection.Parse(text);
            var item = context.Types.Find("Avalonia.Media.TextDecoration")
                ?? throw new InvalidOperationException("The decoration type is unavailable.");
            var create = item.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.IsEmpty)
                ?? throw new InvalidOperationException("The decoration constructor is unavailable.");
            var location = item.GetMembers("Location").OfType<IPropertySymbol>().FirstOrDefault(property => !property.IsStatic &&
                property.SetMethod is { } setter && context.Types.IsAccessible(setter) && property.Type.HasMetadataName("Avalonia.Media.TextDecorationLocation"))
                ?? throw new InvalidOperationException("The decoration location property is unavailable.");
            var constructor = ((INamedTypeSymbol)targetType).InstanceConstructors.FirstOrDefault(method =>
                context.Types.IsAccessible(method) && method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None &&
                method.Parameters[0].Type is INamedTypeSymbol parameter && parameter.OriginalDefinition.HasMetadataName(ClrNames.IEnumerableOfT) &&
                SymbolEqualityComparer.Default.Equals(parameter.TypeArguments[0], item))
                ?? throw new InvalidOperationException("The decoration collection constructor is unavailable.");
            var values = parsed.Select(value => (BoundExpression)new BoundNewExpression(create, [], span)
            {
                SuppressSourceInfo = true,
                Initializers = ImmutableArray.Create(new BoundPropertyInitialization(location,
                    new BoundCastExpression(new BoundConstantExpression((int)value.Location, context.Types.Special(SpecialType.System_Int32), span), location.Type, span)))
            }).ToImmutableArray();
            expression = new BoundNewExpression(constructor, ImmutableArray.Create<BoundExpression>(
                new BoundArrayExpression(values, context.Types.Compilation.CreateArrayTypeSymbol(item), span)), span)
                { SuppressSourceInfo = true, SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            // Unrecognized static decorations already have a tested runtime-failure
            // contract. Fold valid fallbacks without moving those failures to binding.
            return false;
        }
        catch (InvalidOperationException error) { context.Report("XG3001", error.Message, span); }
        return true;
    }
}
