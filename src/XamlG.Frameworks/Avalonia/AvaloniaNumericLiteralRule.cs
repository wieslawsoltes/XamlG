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
        if (targetType is not INamedTypeSymbol target) return false;
        var name = target.MetadataName();
        if (name is not ("Avalonia.Thickness" or "Avalonia.CornerRadius" or "Avalonia.Point" or "Avalonia.Vector" or
            "Avalonia.Size" or "Avalonia.Matrix" or "Avalonia.RelativePoint")) return false;
        if (!TryValues(name, text, out var values, out var relative))
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target.ToDisplayString()}'.", span); return true; }
        var relativePoint = name == "Avalonia.RelativePoint";
        var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
            method.Parameters.Length == values.Length + (relativePoint ? 1 : 0) &&
            method.Parameters.Take(values.Length).All(parameter => parameter.Type.SpecialType == SpecialType.System_Double) &&
            (!relativePoint || method.Parameters.Last().Type.HasMetadataName("Avalonia.RelativeUnit")));
        if (constructor == null)
        { context.Report("XG3001", $"The literal constructor for '{target}' is unavailable.", span); return true; }
        var arguments = values.Select(value => (BoundExpression)new BoundConstantExpression(value, context.Types.Special(SpecialType.System_Double), span)).ToImmutableArray();
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
        if (!Tokens(text, out var tokens)) return false;
        var index = 0;
        bool Next(out double value)
        {
            value = 0;
            return index < tokens.Count && double.TryParse(tokens[index++], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        if (type == "Avalonia.RelativePoint")
        {
            if (tokens.Count != 2) return false;
            relative = tokens[0].EndsWith("%", StringComparison.Ordinal);
            if (relative)
            {
                if (!tokens[1].EndsWith("%", StringComparison.Ordinal)) return false;
                tokens[0] = tokens[0].TrimEnd('%'); tokens[1] = tokens[1].TrimEnd('%');
            }
            if (!Next(out var x) || !Next(out var y)) return false;
            values = new[] { x * (relative ? 0.01 : 1), y * (relative ? 0.01 : 1) };
            return true;
        }
        if (type is "Avalonia.Thickness" or "Avalonia.CornerRadius")
        {
            if (!Next(out var a)) return false;
            if (!Next(out var b)) values = new[] { a, a, a, a };
            else if (!Next(out var c)) values = type == "Avalonia.Thickness" ? new[] { a, b, a, b } : new[] { a, a, b, b };
            else if (Next(out var d)) values = new[] { a, b, c, d };
            else return false;
        }
        else
        {
            values = new double[type == "Avalonia.Matrix" ? 6 : 2];
            for (var i = 0; i < values.Length; i++) if (!Next(out values[i])) return false;
            // Matrix.Parse consumes optional perspective values; the pinned intrinsic emits its six affine components.
            if (type == "Avalonia.Matrix") for (var i = 0; i < 3 && Next(out _); i++) { }
        }
        // Optional reads consume an invalid final token, matching the public parsers' TryReadDouble behavior.
        return index == tokens.Count;
    }

    private static bool Tokens(string text, out List<string> values)
    {
        values = new(); var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && text[index] != ',' && !char.IsWhiteSpace(text[index])) index++;
            if (start == index) return false;
            values.Add(text.Substring(start, index - start));
            var comma = false;
            while (index < text.Length && (text[index] == ',' || char.IsWhiteSpace(text[index])))
            {
                if (text[index++] == ',') { if (comma) return false; comma = true; }
            }
            if (comma && index == text.Length) return false;
        }
        return true;
    }
}
