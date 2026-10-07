using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Preserves ordered collection overloads when the provided value's runtime type
/// is needed. Keys remain statically converted, and the value is bound only once.</summary>
internal sealed class DynamicCollectionBinder(BindingContext context)
{
    public bool TryBind(ObjectBindingBuilder target, BoundMember? member, XamlSyntaxNode syntax,
        NamespaceScope scope, XamlAttributeSyntax? key, NamespaceScope keyScope, IMethodSymbol[] methods)
    {
        if (methods.Select(method => method.Parameters.Last().Type).Distinct(SymbolEqualityComparer.Default).Count() < 2) return false;
        if (syntax is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return false;
        }
        else if (context.Values.PeekValueType(syntax, scope)?.SpecialType != SpecialType.System_Object) return false;

        var value = context.Values.BindNode(syntax, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        BoundExpression? keyValue = null;
        if (key != null)
        {
            var markup = key.Value.StartsWith("{", StringComparison.Ordinal) && !key.Value.StartsWith("{}", StringComparison.Ordinal);
            if (markup)
            {
                keyValue = context.Values.BindText(key.Value, context.Types.Special(SpecialType.System_Object), keyScope, key.ValueSpan);
                if (keyValue == null) return true;
            }
            var first = -1;
            for (var index = 0; index < methods.Length; index++)
            {
                var parameter = methods[index].Parameters[0];
                if (!markup) keyValue = context.Values.TryText(key.Value.StartsWith("{}", StringComparison.Ordinal) ? key.Value.Substring(2) : key.Value,
                    parameter.Type, keyScope, key.ValueSpan, parameter);
                if (keyValue != null && CanAssign(keyValue, parameter.Type)) { first = index; break; }
            }
            if (first < 0) { Error("No collection overload accepts the dictionary key.", key.ValueSpan); return true; }
            methods = methods.Skip(first).ToArray();
            if (methods.Any(method => !CanAssign(keyValue!, method.Parameters[0].Type)))
            { Error("Runtime overload selection is supported only for collection values; dictionary key types must be statically compatible.", key.ValueSpan); return true; }
        }

        var candidates = methods.Where(method => value is BoundConstantExpression { Value: null }
            ? method.Parameters.Last().Type.AcceptsNull()
            : value.Type?.SpecialType == SpecialType.System_Object
                ? context.Types.Compilation.ClassifyCommonConversion(value.Type, method.Parameters.Last().Type).Exists
                : CanAssign(value, method.Parameters.Last().Type)).ToImmutableArray();
        if (candidates.Length == 0) { Error("No compatible collection overload accepts the provided value.", syntax.Span); return true; }
        var selected = candidates[0];
        var dynamic = value.Type?.SpecialType == SpecialType.System_Object && value is not BoundConstantExpression { Value: null } && candidates.Length > 1;
        if (!dynamic)
            value = value is BoundConstantExpression { Value: null } ? new BoundConstantExpression(null, selected.Parameters.Last().Type, value.Span) :
                context.Values.Coerce(value, selected.Parameters.Last().Type, syntax.Span, scope);
        if (value == null) return true;
        var arguments = keyValue == null ? ImmutableArray.Create(value) : ImmutableArray.Create(keyValue, value);
        target.Assignments.Add(new BoundAddAssignment(member, selected, arguments, syntax.Span)
        { Alternatives = dynamic ? candidates : ImmutableArray<IMethodSymbol>.Empty });
        return true;
    }

    private bool CanAssign(BoundExpression value, ITypeSymbol target)
    {
        if (value is BoundConstantExpression { Value: null }) return target.AcceptsNull();
        if (value.Type == null) return false;
        var conversion = context.Types.Compilation.ClassifyConversion(value.Type, target);
        return conversion.IsImplicit && (!conversion.IsNumeric || context.Types.Configuration.AllowImplicitNumericConversions);
    }
    private void Error(string message, TextSpan span) => context.Report("XG1016", message, span);
}
