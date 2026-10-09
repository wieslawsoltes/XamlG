using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Shares immutable path descriptions, never binding instances or namescope-dependent paths.</summary>
internal static class CompiledBindingPathCache
{
    public static BoundExpression Wrap(BindingContext context, BoundExpression path)
    {
        var key = new StringBuilder("Avalonia.CompiledBindingPath:");
        void Part(string value) => key.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        bool Type(ITypeSymbol type)
        {
            if (!Closed(type) || !context.Types.IsAccessible(type)) return false;
            Part(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            return true;
        }
        bool Symbol(ISymbol symbol)
        {
            if (!context.Types.IsAccessible(symbol) || !Type(symbol.ContainingType)) return false;
            Part(symbol.ContainingAssembly.Identity.ToString());
            Part(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
            return true;
        }
        bool Visit(BoundExpression expression)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            Part(expression.GetType().Name);
            if (expression.Type is { } type && !Type(type)) return false;
            switch (expression)
            {
                case BoundConstantExpression constant:
                    Part(constant.Value?.GetType().FullName ?? "null");
                    Part(Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return true;
                case BoundTypeExpression reference: return Type(reference.ReferencedType);
                case BoundCachedExpression { ShareAcrossDocuments: true } cached:
                    Part(cached.Key);
                    return true;
                case BoundNewExpression creation when creation.Constructor.ContainingType.HasMetadataName(AvaloniaBindingMetadata.PathBuilder) && creation.Arguments.IsEmpty && creation.Initializers.IsEmpty:
                    return Symbol(creation.Constructor);
                case BoundCallExpression call when call.Method.ContainingType.HasMetadataName(AvaloniaBindingMetadata.PathBuilder) ||
                    call.Method.ContainingType.HasMetadataName(AvaloniaBindingMetadata.AccessorFactory):
                    return Symbol(call.Method) && call.Method.TypeArguments.All(Type) &&
                        (call.Receiver == null ? call.Method.IsStatic : Visit(call.Receiver)) && call.Arguments.All(Visit);
                case BoundStaticExpression { GeneratedMemberName: null, Member: IFieldSymbol { IsReadOnly: true } field }:
                    return Symbol(field);
                case BoundMethodGroupExpression { Receiver: null, Method.IsStatic: true } method:
                    return Symbol(method.Method);
                case BoundLambdaExpression { IsStatic: true } lambda:
                    Part(lambda.Parameters.Length.ToString(CultureInfo.InvariantCulture));
                    return lambda.Parameters.All(Visit) && Visit(lambda.Body);
                case BoundParameterExpression parameter:
                    Part(parameter.Name);
                    return true;
                case BoundArrayExpression array:
                    Part(array.Values.Length.ToString(CultureInfo.InvariantCulture));
                    return array.Values.All(Visit);
                case BoundCastExpression cast: return Visit(cast.Value);
                // Named element paths carry a namescope service. Runtime strings,
                // custom conversions, method handles and mutable values also stay local.
                default: return false;
            }
        }
        return Visit(path) ? new BoundCachedExpression(key.ToString(), path, path.Span) { ShareAcrossDocuments = true } : path;
    }

    internal static bool Closed(ITypeSymbol type) => type is not ITypeParameterSymbol &&
        (type is not IArrayTypeSymbol array || Closed(array.ElementType)) &&
        (type is not INamedTypeSymbol named || (named.ContainingType == null || Closed(named.ContainingType)) && named.TypeArguments.All(Closed));
}
