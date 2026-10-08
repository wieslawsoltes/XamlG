using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Preserves intrinsic constructor choices, fresh instances and literal source locations.</summary>
public sealed class AvaloniaConstructorLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target) return false;
        if (target.HasMetadataName(ClrNames.Uri))
        {
            var literal = text.Trim();
            var kind = literal.StartsWith("/", StringComparison.Ordinal) ? UriKind.Relative : UriKind.RelativeOrAbsolute;
            if (literal.Length == 0 || !Uri.TryCreate(literal, kind, out _)) return Invalid(context, text, target, span);
            var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.Length == 2 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_String && method.Parameters[1].Type.HasMetadataName("System.UriKind"));
            if (constructor != null)
                expression = Located(context, constructor, span,
                    new BoundConstantExpression(literal, constructor.Parameters[0].Type, span),
                    new BoundCastExpression(new BoundConstantExpression((int)kind, context.Types.Special(SpecialType.System_Int32), span), constructor.Parameters[1].Type, span));
            return true;
        }
        if (target.HasMetadataName("System.TimeSpan"))
        {
            var literal = text.Trim();
            if (!TimeSpan.TryParse(literal, CultureInfo.InvariantCulture, out var value))
            {
                if (literal.Contains(":") || !double.TryParse(literal, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var seconds))
                    return Invalid(context, text, target, span);
                try { value = TimeSpan.FromSeconds(seconds); }
                catch (ArgumentException) { return Invalid(context, text, target, span); }
                catch (OverflowException) { return Invalid(context, text, target, span); }
            }
            var method = target.GetMembers("FromTicks").OfType<IMethodSymbol>().FirstOrDefault(candidate => candidate.IsStatic && candidate.Parameters.Length == 1 &&
                candidate.Parameters[0].Type.SpecialType == SpecialType.System_Int64 && context.Types.IsAccessible(candidate));
            if (method != null) expression = new BoundCallExpression(method, null,
                ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(value.Ticks, method.Parameters[0].Type, span)), span)
                { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
            return true;
        }
        if (target.HasMetadataName(AvaloniaLiteralMetadata.GridLength) || target.HasMetadataName(AvaloniaLiteralMetadata.RowDefinition) || target.HasMetadataName(AvaloniaLiteralMetadata.ColumnDefinition))
        {
            var grid = GridLength(context, text, span);
            if (grid == null) return Invalid(context, text, target, span);
            if (target.HasMetadataName(AvaloniaLiteralMetadata.GridLength)) expression = grid;
            else
            {
                var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.Length == 1 &&
                    method.Parameters[0].Type.HasMetadataName(AvaloniaLiteralMetadata.GridLength));
                if (constructor != null) expression = Located(context, constructor, span, grid);
            }
            return true;
        }
        if (target.HasMetadataName(AvaloniaLiteralMetadata.Cursor))
        {
            var cursorType = context.Types.Find(AvaloniaLiteralMetadata.StandardCursorType);
            var field = cursorType?.GetMembers(text).OfType<IFieldSymbol>().FirstOrDefault(candidate => candidate.HasConstantValue);
            BoundExpression? value = field == null ? null : new BoundStaticExpression(field, field.Type, span);
            if (cursorType != null && long.TryParse(text, out var numeric))
                value = new BoundCastExpression(new BoundConstantExpression(unchecked((int)numeric), context.Types.Special(SpecialType.System_Int32), span), cursorType, span);
            var intrinsic = value != null;
            if (!intrinsic && cursorType != null)
            {
                try
                {
                    var parsed = Parsing.Cursor.Parse(text);
                    value = new BoundCastExpression(new BoundConstantExpression((int)parsed.Type,
                        context.Types.Special(SpecialType.System_Int32), span), cursorType, span);
                }
                catch (ArgumentException) { return Invalid(context, text, target, span); }
            }
            if (value == null) return false;
            var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, cursorType));
            if (constructor != null) expression = Located(context, constructor, span, value) with { SuppressSourceInfo = !intrinsic };
            return true;
        }
        if (target.HasMetadataName("Avalonia.Media.HslColor") || target.HasMetadataName("Avalonia.Media.HsvColor"))
        {
            double[] values;
            if (target.HasMetadataName("Avalonia.Media.HslColor"))
            {
                if (!Parsing.HslColor.TryParse(text, out var value)) return Invalid(context, text, target, span);
                values = new[] { value.A, value.H, value.S, value.L };
            }
            else
            {
                if (!Parsing.HsvColor.TryParse(text, out var value)) return Invalid(context, text, target, span);
                values = new[] { value.A, value.H, value.S, value.V };
            }
            var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.Length == 4 &&
                method.Parameters.All(parameter => parameter.Type.SpecialType == SpecialType.System_Double));
            if (constructor != null) expression = Located(context, constructor, span,
                values.Select(value => (BoundExpression)new BoundConstantExpression(value, context.Types.Special(SpecialType.System_Double), span)).ToArray());
            return true;
        }
        var isColor = target.HasMetadataName(AvaloniaLiteralMetadata.Color);
        var brush = context.Types.Find(AvaloniaMetadata.BrushContract);
        var isBrush = brush != null && context.Types.Compilation.ClassifyCommonConversion(target, brush).IsImplicit;
        if (!isColor && !isBrush) return false;
        var colorText = isColor ? text.Trim() : text;
        if (!Parsing.Color.TryParse(colorText, out var colorValue))
            return isColor && Invalid(context, text, target, span);
        var color = context.Types.Find(AvaloniaLiteralMetadata.Color);
        var packed = new BoundConstantExpression(colorValue.ToUInt32(), context.Types.Special(SpecialType.System_UInt32), span);
        if (isColor)
        {
            var factory = color?.GetMembers("FromUInt32").OfType<IMethodSymbol>().FirstOrDefault(method => method.IsStatic && method.Parameters.Length == 1 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_UInt32 && context.Types.IsAccessible(method));
            if (factory != null) expression = new BoundCallExpression(factory, null, ImmutableArray.Create<BoundExpression>(packed), span);
        }
        else
        {
            var constructor = context.Types.Find(AvaloniaLiteralMetadata.ImmutableSolidColorBrush)?.InstanceConstructors.FirstOrDefault(method =>
                context.Types.IsAccessible(method) && method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_UInt32);
            if (constructor != null) expression = Located(context, constructor, span, packed);
        }
        return true;
    }

    private static BoundNewExpression Located(BindingContext context, IMethodSymbol constructor, TextSpan span, params BoundExpression[] arguments) =>
        new(constructor, arguments.ToImmutableArray(), span) { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };

    private static bool Invalid(BindingContext context, string text, ITypeSymbol type, TextSpan span)
    {
        context.Report("XG3004", $"Unable to parse '{text}' as '{type.ToDisplayString()}'.", span);
        return true;
    }

    private static BoundNewExpression? GridLength(BindingContext context, string text, TextSpan span)
    {
        var value = 0d;
        var unit = "Auto";
        if (!string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            var star = text.EndsWith("*", StringComparison.Ordinal);
            var number = star ? text.Substring(0, text.Length - 1).Trim() : text;
            if (star && number.Length == 0) value = 1;
            else if (!double.TryParse(number, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)) return null;
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) return null;
            unit = star ? "Star" : "Pixel";
        }
        var type = context.Types.Find(AvaloniaLiteralMetadata.GridLength);
        var constructor = type?.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) && method.Parameters.Length == 2 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_Double && method.Parameters[1].Type.HasMetadataName("Avalonia.Controls.GridUnitType"));
        var field = constructor?.Parameters[1].Type.GetMembers(unit).OfType<IFieldSymbol>().FirstOrDefault();
        return constructor == null || field == null ? null : new(constructor, ImmutableArray.Create<BoundExpression>(
            new BoundConstantExpression(value, constructor.Parameters[0].Type, span), new BoundStaticExpression(field, field.Type, span)), span);
    }
}
