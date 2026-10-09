using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Parses path markup into typed context calls and preserves the runtime drawing scope.</summary>
public sealed class AvaloniaGeometryLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (targetType.MetadataName is not ("Geometry" or "StreamGeometry" or "PathGeometry" or "PathFigures") ||
            targetType.MetadataName() is not ("Avalonia.Media.Geometry" or "Avalonia.Media.StreamGeometry" or "Avalonia.Media.PathGeometry" or "Avalonia.Media.PathFigures")) return false;
        try
        {
            var parsed = new Parsing.ParsedGeometry();
            using (var parser = new Parsing.PathMarkupParser(parsed, context.Cancellation)) parser.Parse(text);
            var figures = targetType.MetadataName == "PathFigures";
            var path = figures || targetType.MetadataName == "PathGeometry";
            var owner = context.Types.Find(path ? "Avalonia.Media.PathGeometry" : "Avalonia.Media.StreamGeometry")
                ?? throw new InvalidOperationException("The geometry literal type is unavailable.");
            var constructor = owner.InstanceConstructors.FirstOrDefault(method => method.Parameters.IsEmpty && context.Types.IsAccessible(method))
                ?? throw new InvalidOperationException("The geometry literal constructor is unavailable.");
            // PathGeometry.Open did not populate Figures in older framework versions.
            // Its public parser uses PathGeometryContext directly; preserve that route.
            var open = path ? context.Types.Find("Avalonia.Visuals.Platform.PathGeometryContext")?.InstanceConstructors.FirstOrDefault(method =>
                context.Types.IsAccessible(method) && method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, owner)) : owner.Members("Open").OfType<IMethodSymbol>().FirstOrDefault(method => !method.IsStatic &&
                method.Parameters.IsEmpty && context.Types.IsAccessible(method) && method.ReturnType.HasMetadataName("Avalonia.Media.StreamGeometryContext"));
            if (open == null) throw new InvalidOperationException("The geometry context factory is unavailable.");
            var contextType = path ? open.ContainingType : open.ReturnType;
            var calls = ImmutableArray.CreateBuilder<BoundBuilderCall>(parsed.Calls.Count);
            foreach (var call in parsed.Calls)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var arguments = call.Arguments.Select(Value).ToImmutableArray();
                var method = contextType.GetMembers(call.Method).OfType<IMethodSymbol>().FirstOrDefault(candidate =>
                    !candidate.IsStatic && candidate.ReturnsVoid && context.Types.IsAccessible(candidate) && candidate.Parameters.Length == arguments.Length &&
                    candidate.Parameters.Select((parameter, index) => parameter.RefKind == RefKind.None &&
                        SymbolEqualityComparer.Default.Equals(parameter.Type, arguments[index].Type)).All(valid => valid))
                    ?? throw new InvalidOperationException("The geometry method '" + call.Method + "' is unavailable.");
                calls.Add(new(method, arguments));
            }
            var result = figures ? owner.GetMembers("Figures").OfType<IPropertySymbol>().FirstOrDefault(property =>
                !property.IsStatic && property.GetMethod != null && context.Types.IsAccessible(property.GetMethod) &&
                property.Type.HasMetadataName("Avalonia.Media.PathFigures")) : null;
            if (figures && result == null) throw new InvalidOperationException("The path figures property is unavailable.");
            expression = new BoundScopedInitializationExpression(new BoundNewExpression(constructor, ImmutableArray<BoundExpression>.Empty, span)
                { SuppressSourceInfo = true }, open, calls.ToImmutable(), result, span)
                { SuppressSourceInfo = true, SourceInfoSpan = BoundSourceInfo.ValueLocation(context.Syntax, span) };
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{targetType}': {error.Message}", span); }
        catch (InvalidOperationException error) { context.Report("XG3001", error.Message, span); }
        return true;

        BoundExpression Value(object value)
        {
            if (value is double number) return new BoundConstantExpression(number, context.Types.Special(SpecialType.System_Double), span);
            if (value is bool flag) return new BoundConstantExpression(flag, context.Types.Special(SpecialType.System_Boolean), span);
            if (value is Parsing.Point point) return Vector("Avalonia.Point", point.X, point.Y);
            if (value is Parsing.Size size) return Vector("Avalonia.Size", size.Width, size.Height);
            var type = context.Types.Find(value is Parsing.FillRule ? "Avalonia.Media.FillRule" : "Avalonia.Media.SweepDirection");
            var field = type?.GetMembers(value.ToString()!).OfType<IFieldSymbol>().FirstOrDefault(candidate => candidate.HasConstantValue)
                ?? throw new InvalidOperationException("The geometry enum value is unavailable.");
            return new BoundStaticExpression(field, field.Type, span);
        }

        BoundExpression Vector(string name, double first, double second)
        {
            var constructor = context.Types.Find(name)?.InstanceConstructors.FirstOrDefault(method => context.Types.IsAccessible(method) &&
                method.Parameters.Length == 2 && method.Parameters.All(parameter => parameter.RefKind == RefKind.None && parameter.Type.SpecialType == SpecialType.System_Double))
                ?? throw new InvalidOperationException("The geometry coordinate constructor is unavailable.");
            return new BoundNewExpression(constructor, ImmutableArray.Create(Value(first), Value(second)), span) { SuppressSourceInfo = true };
        }
    }
}
