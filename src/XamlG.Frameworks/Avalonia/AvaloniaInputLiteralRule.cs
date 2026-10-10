using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Parses keyboard gestures at compile time using the upstream grammar.</summary>
public sealed class AvaloniaInputLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target || !target.HasMetadataName("Avalonia.Input.KeyGesture")) return false;
        try
        {
            var parsed = Parsing.KeyGesture.Parse(text);
            var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
                method.Parameters.Length == 2 && method.Parameters.All(parameter => parameter.RefKind == RefKind.None) &&
                method.Parameters[0].Type.HasMetadataName("Avalonia.Input.Key") &&
                method.Parameters[1].Type.HasMetadataName("Avalonia.Input.KeyModifiers"));
            if (constructor == null)
                context.Report("XG3001", "The keyboard gesture constructor is unavailable.", span);
            else
                expression = new BoundNewExpression(constructor, ImmutableArray.Create<BoundExpression>(
                    EnumValue((int)parsed.Key, constructor.Parameters[0].Type),
                    EnumValue((int)parsed.KeyModifiers, constructor.Parameters[1].Type)), span) { SuppressSourceInfo = true };
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target.ToDisplayString()}'.", span); }
        return true;

        BoundExpression EnumValue(int value, ITypeSymbol type) => new BoundCastExpression(
            new BoundConstantExpression(value, context.Types.Special(SpecialType.System_Int32), span), type, span);
    }
}
