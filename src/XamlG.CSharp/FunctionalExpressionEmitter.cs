using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class FunctionalExpressionEmitter(EmissionContext context, ValueEmitter values)
{
    public string Emit(BoundExpression value, string frame)
    {
        switch (value)
        {
            case BoundParameterExpression parameter:
                if (!SyntaxFacts.IsValidIdentifier(parameter.Name))
                { context.Error("Invalid bound parameter identifier.", parameter.Span); return "default!"; }
                return CSharpNames.Identifier(parameter.Name);
            case BoundPropertyAccessExpression property:
                var receiver = "(" + values.Emit(property.Receiver, frame) + ")";
                return property.Property.IsIndexer
                    ? receiver + "[" + string.Join(", ", property.IndexArguments.Select(a => values.Emit(a, frame))) + "]"
                    : receiver + "." + CSharpNames.Identifier(property.Property.Name);
            case BoundFieldAccessExpression field:
                return "(" + values.Emit(field.Receiver, frame) + ")." + CSharpNames.Identifier(field.Field.Name);
            case BoundAssignmentExpression assignment:
                if (assignment.Target is not (BoundPropertyAccessExpression or BoundFieldAccessExpression))
                { context.Error("A bound store requires a property, indexer or field target.", assignment.Span); return "default!"; }
                return values.Emit(assignment.Target, frame) + " = " + values.Emit(assignment.Value, frame);
            case BoundMethodGroupExpression method:
                var owner = method.Method.IsStatic ? method.Method.ContainingType.CSharpName() : "(" + values.Emit(method.Receiver!, frame) + ")";
                return "((" + method.DelegateType.CSharpName() + ")" + owner + "." + CSharpNames.Method(method.Method) + ")";
            case BoundLambdaExpression lambda:
                if (!IsInline(lambda.Body))
                { context.Error("Expression-bodied delegates cannot contain construction-graph, resource-factory or deferred operations.", lambda.Span); return "default!"; }
                var parameters = string.Join(", ", lambda.Parameters.Select(p => p.ParameterType.CSharpName() + " " + Emit(p, frame)));
                return "((" + lambda.DelegateType.CSharpName() + ")(" + (lambda.IsStatic ? "static " : string.Empty) + "(" + parameters + ") => " + values.Emit(lambda.Body, frame) + "))";
            default: throw new InvalidOperationException("Unknown functional bound operation.");
        }
    }

    // These nodes emit statements and own a construction lifetime. Hoisting them out of a
    // lambda would change when they execute and can capture locals in a static delegate.
    private static bool IsInline(BoundExpression expression) => expression is not
        (BoundObjectExpression or BoundDeferredExpression or BoundMarkupExpression or BoundResourceExpression or BoundChoiceExpression) &&
        BoundTraversal.Children(expression, true).All(IsInline);
}
