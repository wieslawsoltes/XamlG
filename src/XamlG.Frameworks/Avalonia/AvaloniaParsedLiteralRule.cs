using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Lowers the public numeric and font parsers that are not XamlX language intrinsics.</summary>
public sealed class AvaloniaParsedLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType is not INamedTypeSymbol target || target.MetadataName is not
            ("PixelPoint" or "PixelSize" or "Vector3D" or "RelativeScalar" or "RelativeRect" or "Transform" or
             "FlexBasis" or "UnicodeRange" or "UnicodeRangeSegment" or "OpenTypeTag" or "FontVariationSettings" or
             "Effect" or "IEffect" or "CacheMode")) return false;
        try
        {
            switch (target.MetadataName())
            {
                case "Avalonia.Media.CacheMode":
                    _ = Parsing.CacheMode.Parse(text);
                    expression = New(Type("Avalonia.Media.BitmapCache"));
                    break;
                case "Avalonia.Media.Effect":
                case "Avalonia.Media.IEffect":
                    expression = Parsing.Effect.Parse(text) switch
                    {
                        Parsing.ImmutableBlurEffect blur => New(Type("Avalonia.Media.ImmutableBlurEffect"), Constant(blur.Radius)),
                        Parsing.ImmutableDropShadowEffect shadow => New(Type("Avalonia.Media.ImmutableDropShadowEffect"),
                            Constant(shadow.OffsetX), Constant(shadow.OffsetY), Constant(shadow.BlurRadius), Color(shadow.Color), Constant(shadow.Opacity)),
                        _ => throw new InvalidOperationException("The parsed effect is unsupported.")
                    };
                    break;
                case "Avalonia.PixelPoint":
                    var point = Parsing.PixelPoint.Parse(text);
                    expression = New(target, Constant(point.X), Constant(point.Y));
                    break;
                case "Avalonia.PixelSize":
                    var size = Parsing.PixelSize.Parse(text);
                    expression = New(target, Constant(size.Width), Constant(size.Height));
                    break;
                case "Avalonia.Vector3D":
                    var vector = Parsing.Vector3D.Parse(text);
                    expression = New(target, Constant(vector.X), Constant(vector.Y), Constant(vector.Z));
                    break;
                case "Avalonia.RelativeScalar":
                    var scalar = Parsing.RelativeScalar.Parse(text);
                    expression = New(target, Constant(scalar.Scalar), Unit(scalar.Unit));
                    break;
                case "Avalonia.RelativeRect":
                    var rect = Parsing.RelativeRect.Parse(text);
                    expression = New(target, Constant(rect.X), Constant(rect.Y), Constant(rect.Width), Constant(rect.Height), Unit(rect.Unit));
                    break;
                case "Avalonia.Media.Transform":
                    // Transform.Parse retains perspective. The Matrix XAML intrinsic,
                    // in contrast, intentionally emits only the six affine components.
                    var matrix = Parsing.Matrix.Parse(text);
                    expression = New(Type("Avalonia.Media.MatrixTransform"), New(Type("Avalonia.Matrix"),
                        Constant(matrix.M11), Constant(matrix.M12), Constant(matrix.M13),
                        Constant(matrix.M21), Constant(matrix.M22), Constant(matrix.M23),
                        Constant(matrix.M31), Constant(matrix.M32), Constant(matrix.M33)));
                    break;
                case "Avalonia.Controls.FlexBasis":
                    var basis = Parsing.FlexBasis.Parse(text);
                    expression = New(target, Constant(basis.Value), Enum("Avalonia.Controls.FlexBasisKind", basis.Kind.ToString()));
                    break;
                case "Avalonia.Media.UnicodeRangeSegment":
                    expression = Segment(Parsing.UnicodeRangeSegment.Parse(text));
                    break;
                case "Avalonia.Media.UnicodeRange":
                    var range = Parsing.UnicodeRange.Parse(text);
                    expression = range.Segments == null ? New(target, Segment(range.Single)) :
                        New(target, Array(Type("Avalonia.Media.UnicodeRangeSegment"), range.Segments.Select(Segment)));
                    break;
                case "Avalonia.Media.Fonts.OpenTypeTag":
                    expression = Tag(Parsing.OpenTypeTag.Parse(text));
                    break;
                case "Avalonia.Media.FontVariationSettings":
                    var settings = Parsing.FontVariationSettings.Parse(text);
                    if (ReferenceEquals(settings, Parsing.FontVariationSettings.Empty))
                    {
                        var empty = target.GetMembers("Empty").OfType<IPropertySymbol>().FirstOrDefault(property =>
                            property.IsStatic && property.GetMethod is { } getter && context.Types.IsAccessible(getter) &&
                            SymbolEqualityComparer.Default.Equals(property.Type, target))
                            ?? throw new InvalidOperationException("The empty font-variation settings are unavailable.");
                        expression = new BoundStaticExpression(empty, target, span) { SuppressSourceInfo = true };
                    }
                    else
                    {
                        var variation = Type("Avalonia.Media.FontVariation");
                        expression = New(target, Array(variation, settings.Variations.Select(value =>
                            New(variation, Tag(value.Tag), Constant(value.Value)))));
                    }
                    break;
                default: return false;
            }
            expression = expression with { SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{target}': {error.Message}", span); }
        catch (InvalidOperationException error) { context.Report("XG3001", error.Message, span); }
        return true;

        INamedTypeSymbol Type(string name) => context.Types.Find(name)
            ?? throw new InvalidOperationException("The literal type '" + name + "' is unavailable.");

        BoundConstantExpression Constant(object value) => new(value, context.Types.Special(value switch
        { double => SpecialType.System_Double, uint => SpecialType.System_UInt32, int => SpecialType.System_Int32,
            _ => throw new InvalidOperationException("Unsupported literal constant.") }), span);

        BoundNewExpression New(INamedTypeSymbol type, params BoundExpression[] arguments)
        {
            var constructor = type.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
                method.Parameters.Length == arguments.Length && method.Parameters.Select((parameter, index) =>
                    parameter.RefKind == RefKind.None && arguments[index].Type is { } argumentType &&
                    context.Types.Compilation.ClassifyCommonConversion(argumentType, parameter.Type).IsImplicit).All(valid => valid))
                ?? throw new InvalidOperationException("The literal constructor for '" + type + "' is unavailable.");
            return new(constructor, arguments.ToImmutableArray(), span) { SuppressSourceInfo = true };
        }

        BoundStaticExpression Enum(string typeName, string name)
        {
            var field = Type(typeName).GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(field => field.HasConstantValue)
                ?? throw new InvalidOperationException("The literal enum member '" + name + "' is unavailable.");
            return new(field, field.Type, span);
        }

        BoundExpression Unit(Parsing.RelativeUnit unit) => Enum("Avalonia.RelativeUnit", unit.ToString());
        BoundNewExpression Segment(Parsing.UnicodeRangeSegment segment) =>
            New(Type("Avalonia.Media.UnicodeRangeSegment"), Constant(segment.Start), Constant(segment.End));
        BoundNewExpression Tag(Parsing.OpenTypeTag tag) => New(Type("Avalonia.Media.Fonts.OpenTypeTag"), Constant((uint)tag));
        BoundCallExpression Color(Parsing.Color color)
        {
            var method = Type(AvaloniaLiteralMetadata.Color).GetMembers("FromUInt32").OfType<IMethodSymbol>().FirstOrDefault(candidate =>
                candidate.IsStatic && context.Types.IsAccessible(candidate) && candidate.Parameters.Length == 1 &&
                candidate.Parameters[0].RefKind == RefKind.None && candidate.Parameters[0].Type.SpecialType == SpecialType.System_UInt32)
                ?? throw new InvalidOperationException("The packed color factory is unavailable.");
            return new(method, null, ImmutableArray.Create<BoundExpression>(Constant(color.ToUInt32())), span);
        }
        BoundArrayExpression Array(INamedTypeSymbol element, IEnumerable<BoundExpression> values) =>
            new(values.ToImmutableArray(), context.Types.Compilation.CreateArrayTypeSymbol(element), span) { SuppressSourceInfo = true };
    }
}
