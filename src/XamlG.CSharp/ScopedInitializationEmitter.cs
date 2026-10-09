using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class ScopedInitializationEmitter(EmissionContext context, ValueEmitter values)
{
    public string Emit(BoundScopedInitializationExpression expression, string frame)
    {
        var type = expression.Creation.Type;
        var open = expression.ScopeFactory;
        var constructor = open.MethodKind == MethodKind.Constructor;
        var scopeType = constructor ? open.ContainingType : open.ReturnType;
        if (type == null || (constructor ? open.Parameters.Length != 1 || open.Parameters[0].RefKind != RefKind.None ||
                !SymbolEqualityComparer.Default.Equals(open.Parameters[0].Type, type) :
                !Instance(open, type) || !open.Parameters.IsEmpty || open.ReturnsVoid) ||
            !scopeType.AllInterfaces.Any(contract => contract.HasMetadataName("System.IDisposable")) && !scopeType.HasMetadataName("System.IDisposable") ||
            expression.Calls.Any(call => !Instance(call.Method, scopeType) || !call.Method.ReturnsVoid ||
                call.Method.Parameters.Length != call.Arguments.Length || call.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None)) ||
            expression.ResultProperty is { } property && (property.GetMethod == null || !Instance(property.GetMethod, type) || property.IsIndexer))
        {
            context.Error("A scoped initializer requires an IDisposable scope, instance calls and an optional result property on its owner.", expression.Span);
            return "default!";
        }
        var owner = context.Locals.Declare(type.CSharpName(), values.Emit(expression.Creation, frame), "initialized", inferred: true);
        var scope = context.Temporary("scope");
        var acquire = constructor ? "new " + scopeType.CSharpName() + "(" + owner + ")" : owner + "." + CSharpNames.Method(open) + "()";
        context.Writer.Open("using (var " + scope + " = " + acquire + ")");
        foreach (var call in expression.Calls)
        {
            var arguments = values.EmitArguments(call.Method, call.Arguments, frame);
            context.Writer.Line(scope + "." + CSharpNames.Method(call.Method) + "(" + string.Join(", ", arguments) + ");");
        }
        context.Writer.Close();
        return expression.ResultProperty is { } result ? owner + "." + CSharpNames.Identifier(result.Name) + "!" : owner;
    }

    private static bool Instance(IMethodSymbol method, ITypeSymbol type)
    {
        if (method.IsStatic) return false;
        if (SymbolEqualityComparer.Default.Equals(method.ContainingType, type) ||
            type.AllInterfaces.Any(contract => SymbolEqualityComparer.Default.Equals(method.ContainingType, contract))) return true;
        for (var current = (type as INamedTypeSymbol)?.BaseType; current != null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(method.ContainingType, current)) return true;
        return false;
    }
}
