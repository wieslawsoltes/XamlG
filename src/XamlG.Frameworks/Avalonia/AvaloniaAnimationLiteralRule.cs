using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Evaluates the private upstream animation parsers and emits public typed construction.</summary>
public sealed class AvaloniaAnimationLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target || target.MetadataName is not ("Cue" or "IterationCount" or "KeySpline" or "Easing")) return false;
        var name = target.MetadataName();
        if (name is not ("Avalonia.Animation.Cue" or "Avalonia.Animation.IterationCount" or
            "Avalonia.Animation.KeySpline" or "Avalonia.Animation.Easings.Easing")) return false;
        BoundExpression Constant(object value, SpecialType type) => new BoundConstantExpression(value, context.Types.Special(type), span);
        BoundNewExpression Construct(INamedTypeSymbol? type, params BoundExpression[] arguments)
        {
            var constructor = type?.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
                method.Parameters.Length == arguments.Length && method.Parameters.Select((parameter, index) =>
                    parameter.RefKind == RefKind.None && SymbolEqualityComparer.Default.Equals(parameter.Type, arguments[index].Type)).All(equal => equal));
            if (constructor == null) throw new MissingMemberException("The literal constructor for '" + type + "' is unavailable.");
            // The previous Parse call does not attach object source-info, including
            // when it appears inside a text-only resource element.
            return new(constructor, arguments.ToImmutableArray(), span) { SuppressSourceInfo = true };
        }
        BoundNewExpression Spline(Parsing.KeySpline spline) => Construct(context.Types.Find("Avalonia.Animation.KeySpline"),
            Constant(spline.ControlPointX1, SpecialType.System_Double), Constant(spline.ControlPointY1, SpecialType.System_Double),
            Constant(spline.ControlPointX2, SpecialType.System_Double), Constant(spline.ControlPointY2, SpecialType.System_Double));
        try
        {
            switch (name)
            {
                case "Avalonia.Animation.Cue":
                    expression = Construct(target, Constant(Parsing.Cue.Parse(text, CultureInfo.InvariantCulture).CueValue, SpecialType.System_Double));
                    break;
                case "Avalonia.Animation.IterationCount":
                    var iterations = Parsing.IterationCount.Parse(text);
                    var type = context.Types.Find("Avalonia.Animation.IterationType");
                    var field = type?.GetMembers(iterations.RepeatType.ToString()).OfType<IFieldSymbol>().SingleOrDefault();
                    if (field == null) throw new MissingMemberException("The animation iteration type contract is unavailable.");
                    expression = Construct(target, Constant(iterations.Value, SpecialType.System_UInt64), new BoundStaticExpression(field, field.Type, span));
                    break;
                case "Avalonia.Animation.KeySpline":
                    expression = Spline(Parsing.KeySpline.Parse(text, CultureInfo.InvariantCulture));
                    break;
                default:
                    expression = Parsing.Easing.Parse<BoundExpression>(text, easingName =>
                    {
                        // The upstream factory contains framework subtypes, never
                        // similarly named classes supplied by the application.
                        var easing = target.ContainingAssembly.GetTypeByMetadataName("Avalonia.Animation.Easings." + easingName);
                        return easing is { IsAbstract: false } && context.Types.IsAccessible(easing) &&
                            context.Types.Compilation.ClassifyCommonConversion(easing, target).IsImplicit
                            ? Construct(easing) : null;
                    }, spline => Construct(context.Types.Find("Avalonia.Animation.Easings.SplineEasing"), Spline(spline)));
                    break;
            }
        }
        catch (MissingMemberException error) { context.Report("XG3001", error.Message, span); }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException or InvalidCastException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target.ToDisplayString()}'.", span); }
        return true;
    }
}
