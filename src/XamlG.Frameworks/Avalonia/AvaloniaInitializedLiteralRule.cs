using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Runs the upstream font-feature and shadow parsers before C# emission.</summary>
public sealed class AvaloniaInitializedLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target ||
            !(target.HasMetadataName("Avalonia.Media.FontFeature") || target.HasMetadataName("Avalonia.Media.BoxShadow") ||
              target.HasMetadataName("Avalonia.Media.BoxShadows"))) return false;
        try
        {
            if (target.HasMetadataName("Avalonia.Media.FontFeature"))
            {
                var parsed = Parsing.FontFeature.Parse(text);
                expression = Initialize(target, ("Tag", parsed.Tag), ("Start", parsed.Start), ("End", parsed.End), ("Value", parsed.Value));
            }
            else if (target.HasMetadataName("Avalonia.Media.BoxShadow")) expression = Shadow(Parsing.BoxShadow.Parse(text));
            else
            {
                var parsed = Parsing.BoxShadows.Parse(text);
                if (parsed.Count == 0) expression = Initialize(target);
                else
                {
                    var constructor = target.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
                        method.Parameters.Length == (parsed.Count == 1 ? 1 : 2) && method.Parameters.All(parameter => parameter.RefKind == RefKind.None) &&
                        method.Parameters[0].Type.HasMetadataName("Avalonia.Media.BoxShadow") &&
                        (parsed.Count == 1 || method.Parameters[1].Type is IArrayTypeSymbol { Rank: 1 } array &&
                            array.ElementType.HasMetadataName("Avalonia.Media.BoxShadow")))
                        ?? throw new InvalidOperationException("The box-shadow collection constructor is unavailable.");
                    var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                    arguments.Add(Shadow(parsed[0]));
                    if (parsed.Count > 1)
                        arguments.Add(new BoundArrayExpression(
                            Enumerable.Range(1, parsed.Count - 1).Select(index => (BoundExpression)Shadow(parsed[index])).ToImmutableArray(),
                            (IArrayTypeSymbol)constructor.Parameters[1].Type, span));
                    expression = new BoundNewExpression(constructor, arguments.ToImmutable(), span) { SuppressSourceInfo = true };
                }
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target.ToDisplayString()}'.", span); }
        catch (InvalidOperationException error) { context.Report("XG3001", error.Message, span); }
        return true;

        BoundNewExpression Shadow(Parsing.BoxShadow parsed)
        {
            var shadow = context.Types.Find("Avalonia.Media.BoxShadow") ?? throw new InvalidOperationException("The box-shadow type is unavailable.");
            var color = context.Types.Find(AvaloniaLiteralMetadata.Color)?.GetMembers("FromUInt32").OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.IsStatic && context.Types.IsAccessible(method) && method.Parameters.Length == 1 &&
                    method.Parameters[0].RefKind == RefKind.None && method.Parameters[0].Type.SpecialType == SpecialType.System_UInt32)
                ?? throw new InvalidOperationException("The packed color factory is unavailable.");
            return Initialize(shadow, ("IsInset", parsed.IsInset), ("OffsetX", parsed.OffsetX), ("OffsetY", parsed.OffsetY),
                ("Blur", parsed.Blur), ("Spread", parsed.Spread),
                ("Color", new BoundCallExpression(color, null, ImmutableArray.Create<BoundExpression>(
                    new BoundConstantExpression(parsed.Color.ToUInt32(), color.Parameters[0].Type, span)), span)));
        }

        BoundNewExpression Initialize(INamedTypeSymbol type, params (string Name, object Value)[] properties)
        {
            var constructor = type.InstanceConstructors.FirstOrDefault(method => method.Parameters.IsEmpty && context.Types.IsAccessible(method))
                ?? throw new InvalidOperationException("The parameterless literal constructor is unavailable.");
            var initializers = properties.Select(value =>
            {
                var property = type.GetMembers(value.Name).OfType<IPropertySymbol>().FirstOrDefault(candidate =>
                    !candidate.IsStatic && !candidate.IsIndexer && candidate.SetMethod != null && context.Types.IsAccessible(candidate.SetMethod))
                    ?? throw new InvalidOperationException("The literal initializer '" + value.Name + "' is unavailable.");
                return new BoundPropertyInitialization(property, value.Value as BoundExpression ?? new BoundConstantExpression(value.Value, property.Type, span));
            }).ToImmutableArray();
            return new(constructor, ImmutableArray<BoundExpression>.Empty, span) { Initializers = initializers, SuppressSourceInfo = true };
        }
    }
}
