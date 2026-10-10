using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Lowers vector-like literals using framework-independent numeric parsing and resolved constructors.</summary>
public sealed class AvaloniaNumericLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target || target.MetadataName is not
            ("Thickness" or "CornerRadius" or "Point" or "Vector" or "Size" or "Matrix" or "RelativePoint" or "Rect" or "PixelRect")) return false;
        var name = target.MetadataName();
        if (name is not ("Avalonia.Thickness" or "Avalonia.CornerRadius" or "Avalonia.Point" or "Avalonia.Vector" or
            "Avalonia.Size" or "Avalonia.Matrix" or "Avalonia.RelativePoint" or "Avalonia.Rect" or "Avalonia.PixelRect")) return false;
        if (!TryValues(name, text, out var values, out var relative))
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target.ToDisplayString()}'.", span); return true; }
        var relativePoint = name == "Avalonia.RelativePoint";
        var scalarType = name == "Avalonia.PixelRect" ? SpecialType.System_Int32 : SpecialType.System_Double;
        var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
            method.Parameters.Length == values.Length + (relativePoint ? 1 : 0) &&
            method.Parameters.Take(values.Length).All(parameter => parameter.Type.SpecialType == scalarType) &&
            (!relativePoint || method.Parameters.Last().Type.HasMetadataName("Avalonia.RelativeUnit")));
        if (constructor == null)
        { context.Report("XG3001", $"The literal constructor for '{target}' is unavailable.", span); return true; }
        var arguments = values.Select(value => (BoundExpression)new BoundConstantExpression(
            scalarType == SpecialType.System_Int32 ? (object)(int)value : value, context.Types.Special(scalarType), span)).ToImmutableArray();
        if (relativePoint)
        {
            var field = constructor.Parameters.Last().Type.GetMembers(relative ? "Relative" : "Absolute").OfType<IFieldSymbol>().FirstOrDefault();
            if (field == null) { context.Report("XG3001", "The relative-coordinate unit contract is unavailable.", span); return true; }
            arguments = arguments.Add(new BoundStaticExpression(field, field.Type, span));
        }
        expression = new BoundNewExpression(constructor, arguments, span) { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        return true;
    }

    private static bool TryValues(string type, string text, out double[] values, out bool relative)
    {
        values = Array.Empty<double>(); relative = false;
        try
        {
            using var tokens = new Parsing.SpanStringTokenizer(text, CultureInfo.InvariantCulture);
            if (type == "Avalonia.RelativePoint")
            {
                var x = tokens.ReadString();
                var y = tokens.ReadString();
                relative = x.EndsWith("%", StringComparison.Ordinal);
                if (relative)
                {
                    if (!y.EndsWith("%", StringComparison.Ordinal)) return false;
                    x = x.TrimEnd('%'); y = y.TrimEnd('%');
                }
                values = new[] { double.Parse(x, CultureInfo.InvariantCulture) * (relative ? 0.01 : 1),
                    double.Parse(y, CultureInfo.InvariantCulture) * (relative ? 0.01 : 1) };
            }
            else if (type is "Avalonia.Thickness" or "Avalonia.CornerRadius")
            {
                var a = tokens.ReadDouble();
                if (!tokens.TryReadDouble(out var b)) values = new[] { a, a, a, a };
                else if (!tokens.TryReadDouble(out var c)) values = type == "Avalonia.Thickness" ? new[] { a, b, a, b } : new[] { a, a, b, b };
                else values = new[] { a, b, c, tokens.ReadDouble() };
            }
            else
            {
                values = new double[type == "Avalonia.Matrix" ? 6 : type is "Avalonia.Rect" or "Avalonia.PixelRect" ? 4 : 2];
                for (var i = 0; i < values.Length; i++)
                    values[i] = type == "Avalonia.PixelRect" ? tokens.ReadInt32() : tokens.ReadDouble();
                // Matrix.Parse consumes optional perspective values; the pinned intrinsic emits its six affine components.
                if (type == "Avalonia.Matrix") for (var i = 0; i < 3 && tokens.TryReadDouble(out _); i++) { }
            }
            // Disposing the upstream tokenizer validates that all input was consumed,
            // including its precise behavior for failed optional numeric reads.
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
