using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Resolves interface-valued literals to framework parsing symbols without executing framework code.</summary>
public sealed class AvaloniaTextConversionRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        var ownerName = targetType.HasMetadataName(AvaloniaMetadata.BrushContract) ? AvaloniaMetadata.Brush :
            targetType.HasMetadataName(AvaloniaMetadata.TransformContract) ? AvaloniaMetadata.Transform : null;
        if (ownerName != null)
        {
            var parse = context.Types.Find(ownerName)?.Members(ClrNames.Parse).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String &&
                    context.Types.IsAccessible(m) && context.Types.Compilation.ClassifyCommonConversion(m.ReturnType, targetType).IsImplicit);
            if (parse != null) expression = new BoundParseExpression(text, parse, targetType, span);
            else context.Report("XG3001", $"The Avalonia parser for '{targetType}' could not be resolved.", span);
            return true;
        }
        if (targetType.HasMetadataName(AvaloniaMetadata.RowDefinitions) || targetType.HasMetadataName(AvaloniaMetadata.ColumnDefinitions))
        {
            var constructor = ((INamedTypeSymbol)targetType).InstanceConstructors.FirstOrDefault(m =>
                m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String && context.Types.IsAccessible(m));
            if (constructor != null)
                expression = new BoundNewExpression(constructor,
                    ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(text, constructor.Parameters[0].Type, span)), span);
            return constructor != null;
        }
        return false;
    }
}
