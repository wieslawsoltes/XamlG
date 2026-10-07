using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Converts keys once before value overload selection; only the last argument can be dynamic.</summary>
internal sealed class CollectionKeyBinder(BindingContext context)
{
    public bool TryBind(XamlAttributeSyntax? key, NamespaceScope scope, ref IMethodSymbol[] methods, out BoundExpression? value)
    {
        value = null;
        if (key == null) return true;
        var markup = key.Value.StartsWith("{", StringComparison.Ordinal) && !key.Value.StartsWith("{}", StringComparison.Ordinal);
        if (markup)
        {
            value = context.Values.BindText(key.Value, context.Types.Special(SpecialType.System_Object), scope, key.ValueSpan);
            if (value == null) return false;
        }
        var first = -1;
        for (var index = 0; index < methods.Length; index++)
        {
            var parameter = methods[index].Parameters[0];
            if (!markup) value = context.Values.TryText(key.Value.StartsWith("{}", StringComparison.Ordinal) ? key.Value.Substring(2) : key.Value,
                parameter.Type, scope, key.ValueSpan, parameter);
            if (value != null && CanAssign(value, parameter.Type)) { first = index; break; }
            if (!markup && PrimitiveValueParser.IsScalar(parameter.Type))
            { context.Report("XG1008", $"Cannot convert '{key.Value}' to '{parameter.Type.ToDisplayString()}'.", key.ValueSpan); return false; }
        }
        if (first < 0) { Error("No collection overload accepts the dictionary key."); return false; }
        methods = methods.Skip(first).ToArray();
        var keyValue = value!;
        if (methods.Any(method => !CanAssign(keyValue, method.Parameters[0].Type)))
        { Error("Runtime overload selection is supported only for collection values; dictionary key types must be statically compatible."); return false; }
        return true;

        void Error(string message) => context.Report("XG1016", message, key.ValueSpan);
    }

    private bool CanAssign(BoundExpression value, ITypeSymbol target)
    {
        if (value is BoundConstantExpression { Value: null }) return target.AcceptsNull();
        if (value.Type == null) return false;
        var conversion = context.Types.Compilation.ClassifyConversion(value.Type, target);
        return conversion.IsImplicit && (!conversion.IsNumeric || context.Types.Configuration.AllowImplicitNumericConversions);
    }
}
