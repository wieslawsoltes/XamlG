using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Parses operation lists during compilation and emits the framework's ordered typed builder calls.</summary>
public sealed class AvaloniaTransformLiteralRule : IXamlTextConversionRule
{
    public bool TryConvert(BindingContext context, string text, ITypeSymbol targetType, NamespaceScope scope,
        TextSpan span, ISymbol? member, out BoundExpression? expression)
    {
        expression = null;
        if (!targetType.HasMetadataName(AvaloniaMetadata.TransformContract) &&
            !targetType.HasMetadataName(AvaloniaLiteralMetadata.TransformOperations)) return false;
        try
        {
            var parsed = Parsing.TransformParser.Parse(text);
            var operations = context.Types.Find(AvaloniaLiteralMetadata.TransformOperations)
                ?? throw new InvalidOperationException("The transform-operations type is unavailable.");
            var location = BoundSourceInfo.ValueLocation(context.Syntax, span);
            if (ReferenceEquals(parsed, Parsing.ParsedTransformOperations.Identity))
            {
                var identity = operations.GetMembers("Identity").OfType<IPropertySymbol>().FirstOrDefault(property =>
                    property.IsStatic && property.GetMethod is { } getter && context.Types.IsAccessible(getter) &&
                    SymbolEqualityComparer.Default.Equals(property.Type, operations))
                    ?? throw new InvalidOperationException("The identity transform is unavailable.");
                expression = new BoundStaticExpression(identity, operations, span) { SourceInfoSpan = location, SuppressSourceInfo = true };
            }
            else
            {
                var create = operations.GetMembers("CreateBuilder").OfType<IMethodSymbol>().FirstOrDefault(method =>
                    method.IsStatic && !method.IsGenericMethod && context.Types.IsAccessible(method) && method.Parameters.Length == 1 &&
                    method.Parameters[0].RefKind == RefKind.None && method.Parameters[0].Type.SpecialType == SpecialType.System_Int32 &&
                    method.ReturnType.HasMetadataName(AvaloniaLiteralMetadata.TransformOperations + "+Builder"))
                    ?? throw new InvalidOperationException("The transform builder factory is unavailable.");
                var builder = (INamedTypeSymbol)create.ReturnType;
                var calls = ImmutableArray.CreateBuilder<BoundBuilderCall>(parsed.Operations.Count);
                foreach (var operation in parsed.Operations)
                {
                    var matrix = operation.Method == "AppendMatrix";
                    var append = builder.GetMembers(operation.Method).OfType<IMethodSymbol>().FirstOrDefault(method =>
                        !method.IsStatic && !method.IsGenericMethod && method.ReturnsVoid && context.Types.IsAccessible(method) &&
                        method.Parameters.Length == (matrix ? 1 : operation.Arguments.Length) && method.Parameters.All(parameter =>
                            parameter.RefKind == RefKind.None && (matrix ? parameter.Type.HasMetadataName("Avalonia.Matrix") :
                                parameter.Type.SpecialType == SpecialType.System_Double)))
                        ?? throw new InvalidOperationException("The transform builder method '" + operation.Method + "' is unavailable.");
                    var arguments = operation.Arguments.Select(value => (BoundExpression)new BoundConstantExpression(
                        value, context.Types.Special(SpecialType.System_Double), span)).ToImmutableArray();
                    if (matrix)
                    {
                        var constructor = ((INamedTypeSymbol)append.Parameters[0].Type).InstanceConstructors.FirstOrDefault(method =>
                            context.Types.IsAccessible(method) && method.Parameters.Length == 6 && method.Parameters.All(parameter =>
                                parameter.RefKind == RefKind.None && parameter.Type.SpecialType == SpecialType.System_Double))
                            ?? throw new InvalidOperationException("The six-value matrix constructor is unavailable.");
                        arguments = ImmutableArray.Create<BoundExpression>(new BoundNewExpression(constructor, arguments, span) { SuppressSourceInfo = true });
                    }
                    calls.Add(new(append, arguments));
                }
                var build = builder.GetMembers("Build").OfType<IMethodSymbol>().FirstOrDefault(method =>
                    !method.IsStatic && !method.IsGenericMethod && method.Parameters.IsEmpty && context.Types.IsAccessible(method) &&
                    SymbolEqualityComparer.Default.Equals(method.ReturnType, operations))
                    ?? throw new InvalidOperationException("The transform builder result method is unavailable.");
                var creation = new BoundCallExpression(create, null, ImmutableArray.Create<BoundExpression>(
                    new BoundConstantExpression(calls.Count, create.Parameters[0].Type, span)), span);
                expression = new BoundBuilderExpression(creation, calls.ToImmutable(), build, span) { SourceInfoSpan = location, SuppressSourceInfo = true };
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { context.Report("XG3004", $"Unable to parse '{text}' as '{targetType.ToDisplayString()}': {error.Message}", span); }
        catch (InvalidOperationException error) { context.Report("XG3001", error.Message, span); }
        return true;
    }
}
