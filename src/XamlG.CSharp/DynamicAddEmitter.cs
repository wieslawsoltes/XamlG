using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Interns typed collection dispatch helpers. Only the final argument is dynamic;
/// receivers and keys retain their statically resolved types.</summary>
internal sealed class DynamicAddEmitter(EmissionContext context)
{
    private readonly Dictionary<string, (string Name, ITypeSymbol Receiver, BoundMember? Collection, ImmutableArray<IMethodSymbol> Methods)> _plans = new(StringComparer.Ordinal);

    public string Register(ITypeSymbol receiver, BoundMember? collection, ImmutableArray<IMethodSymbol> methods)
    {
        var key = receiver.CSharpName() + "\n" + collection?.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "\n" + string.Join("\n", methods.Select(method => method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        if (_plans.TryGetValue(key, out var existing)) return existing.Name;
        var name = "__XamlDynamicAdd_" + CSharpNames.StableId(key);
        _plans.Add(key, (name, receiver, collection, methods));
        return name;
    }

    public void Emit()
    {
        var writer = context.Writer;
        foreach (var plan in _plans.Values)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var parameters = plan.Methods[0].Parameters;
            var key = parameters.Length == 2 ? ", " + parameters[0].Type.CSharpName() + " __key" : string.Empty;
            writer.Open("private static void " + plan.Name + "(" + plan.Receiver.CSharpName() + " __target" + key + ", object? __value)");
            IMethodSymbol? acceptsNull = null;
            var catchAll = false;
            for (var index = 0; index < plan.Methods.Length; index++)
            {
                var method = plan.Methods[index];
                var type = method.Parameters.Last().Type;
                if (type.AcceptsNull()) acceptsNull ??= method;
                var patternType = type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                    ? nullable.TypeArguments[0] : type;
                catchAll = type.SpecialType == SpecialType.System_Object;
                writer.Open((index == 0 ? "if" : "else if") + " (" + (catchAll ? "true" : "__value is " + patternType.CSharpName()) + ")");
                Call(method, plan.Collection);
                writer.Close();
                if (catchAll) break;
            }
            if (!catchAll)
            {
                writer.Open("else");
                writer.Open("if (__value is null)");
                if (acceptsNull != null) Call(acceptsNull, plan.Collection);
                else writer.Line("throw new global::System.NullReferenceException(\"A null XAML value cannot be unboxed into a non-nullable collection item.\");");
                writer.Close();
                writer.Line("throw new global::System.InvalidCastException(\"No XAML collection overload accepts the runtime value.\");");
                writer.Close();
            }
            writer.Close();
        }

        void Call(IMethodSymbol method, BoundMember? collection)
        {
            var key = method.Parameters.Length == 2 ? "(" + method.Parameters[0].Type.CSharpName() + ")__key, " : string.Empty;
            var receiver = collection == null ? "__target" : AssignmentEmitter.Get(collection, "__target");
            writer.Line("((" + method.ContainingType.CSharpName() + ")" + receiver + ")." + CSharpNames.Method(method) + "(" + key +
                "(" + method.Parameters.Last().Type.CSharpName() + ")__value!);");
            writer.Line("return;");
        }
    }
}
