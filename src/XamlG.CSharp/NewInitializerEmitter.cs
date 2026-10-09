using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class NewInitializerEmitter(EmissionContext context, ValueEmitter values)
{
    public string Emit(BoundNewExpression creation, string constructed, string frame)
    {
        var initializers = creation.Initializers;
        foreach (var initializer in initializers)
            if (initializer.Property is { IsStatic: true } or { IsIndexer: true } or { SetMethod: null })
                throw new InvalidOperationException("A constructor initializer requires a writable instance property.");

        // These values emit no statements. Keeping them in the C# initializer
        // preserves constructor/setter order and uses ordinary init-only access.
        if (initializers.Select(initializer => initializer.Property.Name).Distinct(StringComparer.Ordinal).Count() == initializers.Length &&
            initializers.All(initializer => Inline(initializer.Value) &&
            SymbolEqualityComparer.Default.Equals(initializer.Property.ContainingType, creation.Constructor.ContainingType)))
            return constructed + " { " + string.Join(", ", initializers.Select(initializer =>
                CSharpNames.Identifier(initializer.Property.Name) + " = " + values.Emit(initializer.Value, frame))) + " }";

        // A value may lower into statements or require target services. Construct
        // first, then evaluate and assign each value before lowering the next.
        var local = context.Locals.Declare(creation.Constructor.ContainingType.CSharpName(), constructed, "initialized", inferred: true);
        foreach (var initializer in initializers)
        {
            var property = initializer.Property;
            var value = values.Emit(initializer.Value, frame);
            var assignment = property.SetMethod!.IsInitOnly
                ? context.InitSetter(property.SetMethod) + "(" + (property.ContainingType.IsValueType ? "ref " : string.Empty) + local + ", " + value + ")"
                : (property.ContainingType.IsValueType ? local : "((" + property.ContainingType.CSharpName() + ")" + local + ")") +
                    "." + CSharpNames.Identifier(property.Name) + " = " + value;
            context.Writer.Line(assignment + ";");
        }
        return local;
    }

    private static bool Inline(BoundExpression expression) => expression switch
    {
        BoundConstantExpression or BoundEnumExpression or BoundStaticExpression or BoundTypeExpression => true,
        BoundCastExpression cast => Inline(cast.Value),
        // Exact scalar arguments stay in the call; other arguments may spill.
        BoundCallExpression { Method.IsStatic: true, RuntimeDependency: null } call =>
            call.Arguments.Length == call.Method.Parameters.Length && call.Arguments.Select((argument, index) =>
                argument is BoundConstantExpression constant && ValueEmitter.ConstantType(constant.Value) is var type &&
                type != SpecialType.None && type == call.Method.Parameters[index].Type.SpecialType).All(value => value),
        _ => false
    };
}
