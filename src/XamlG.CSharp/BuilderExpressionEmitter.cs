using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class BuilderExpressionEmitter(EmissionContext context, ValueEmitter values)
{
    public string Emit(BoundBuilderExpression expression, string frame)
    {
        var type = expression.Creation.Type;
        if (type == null || !Instance(expression.ResultMethod) || expression.ResultMethod.ReturnsVoid ||
            !expression.ResultMethod.Parameters.IsEmpty || expression.Calls.Any(call => !Instance(call.Method) ||
                !call.Method.ReturnsVoid || call.Method.Parameters.Length != call.Arguments.Length ||
                call.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None)))
        {
            context.Error("A bound builder requires instance methods on its exact type and a parameterless result method.", expression.Span);
            return "default!";
        }
        var creation = values.Emit(expression.Creation, frame);
        var local = context.Locals.Declare(type.CSharpName(), creation, "builder", inferred: true);
        foreach (var call in expression.Calls)
        {
            var arguments = values.EmitArguments(call.Method, call.Arguments, frame);
            // Calling on the original local preserves mutations of value-type builders.
            context.Writer.Line(local + "." + CSharpNames.Method(call.Method) + "(" + string.Join(", ", arguments) + ");");
        }
        return local + "." + CSharpNames.Method(expression.ResultMethod) + "()";

        bool Instance(IMethodSymbol method) => !method.IsStatic && SymbolEqualityComparer.Default.Equals(method.ContainingType, type);
    }
}
