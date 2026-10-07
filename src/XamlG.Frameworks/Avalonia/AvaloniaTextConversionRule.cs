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
        // ITransform follows Avalonia's TransformConverter contract: operation lists
        // retain their primitive transforms for interpolation. Transform.Parse accepts
        // only matrix text and is not the parser for interface-valued XAML literals.
        var ownerName = targetType.HasMetadataName(AvaloniaMetadata.BrushContract) ? AvaloniaMetadata.Brush :
            targetType.HasMetadataName(AvaloniaMetadata.TransformContract) ? AvaloniaLiteralMetadata.TransformOperations : null;
        if (ownerName != null)
        {
            var parse = context.Types.Find(ownerName)?.Members(ClrNames.Parse).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.IsStatic && !m.IsGenericMethod && m.Parameters.Length == 1 &&
                    m.Parameters[0].Type.SpecialType == SpecialType.System_String && context.Types.IsAccessible(m) &&
                    context.Types.Compilation.ClassifyCommonConversion(m.ReturnType, targetType).IsImplicit);
            if (parse != null) expression = new BoundParseExpression(text, parse, targetType, span);
            else context.Report("XG3001", $"The Avalonia parser for '{targetType}' could not be resolved.", span);
            return true;
        }
        return false;
    }
}
