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
        NamespaceScope scope, BoundExpression? keyValue, IMethodSymbol[] methods)
    {
        if (methods.Length == 0) return false;
        if (syntax is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return false;
        }
        else if (context.Values.PeekValueType(syntax, scope, target.NameScopeId)?.SpecialType != SpecialType.System_Object) return false;

        var value = context.Values.BindNode(syntax, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        var compatible = methods.Where(method => value is BoundConstantExpression { Value: null }
            ? method.Parameters.Last().Type.AcceptsNull()
            : value.Type?.SpecialType == SpecialType.System_Object
                ? context.Types.Compilation.ClassifyCommonConversion(value.Type, method.Parameters.Last().Type).Exists
                : CanAssign(value, method.Parameters.Last().Type));
        var candidates = ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (var method in compatible)
        {
            var type = method.Parameters.Last().Type;
            if (candidates.Any(previous => Subsumes(previous.Parameters.Last().Type, type))) continue;
            candidates.Add(method);
        }
        if (candidates.Count == 0) { Error("No compatible collection overload accepts the provided value.", syntax.Span); return true; }
        var selected = candidates[0];
        var dynamic = value.Type?.SpecialType == SpecialType.System_Object && value is not BoundConstantExpression { Value: null } && candidates.Count > 1;
        if (!dynamic)
            value = value is BoundConstantExpression { Value: null } ? new BoundConstantExpression(null, selected.Parameters.Last().Type, value.Span) :
                context.Values.Coerce(value, selected.Parameters.Last().Type, syntax.Span, scope);
        if (value == null) return true;
        var arguments = keyValue == null ? ImmutableArray.Create(value) : ImmutableArray.Create(keyValue, value);
        target.Assignments.Add(new BoundAddAssignment(member, selected, arguments, syntax.Span)
        { Alternatives = dynamic ? candidates.ToImmutable() : ImmutableArray<IMethodSymbol>.Empty });
        return true;
    }

    private bool CanAssign(BoundExpression value, ITypeSymbol target)
    {
        if (value is BoundConstantExpression { Value: null }) return target.AcceptsNull();
        if (value.Type == null) return false;
        var conversion = context.Types.Compilation.ClassifyConversion(value.Type, target);
        return conversion.IsImplicit && (!conversion.IsNumeric || context.Types.Configuration.AllowImplicitNumericConversions);
    }
    private bool Subsumes(ITypeSymbol previous, ITypeSymbol candidate)
    {
        ITypeSymbol RuntimeType(ITypeSymbol type) => type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            ? nullable.TypeArguments[0] : type;
        var conversion = context.Types.Compilation.ClassifyConversion(RuntimeType(candidate), RuntimeType(previous));
        return conversion.IsImplicit && !conversion.IsNumeric && !conversion.IsUserDefined && (!candidate.AcceptsNull() || previous.AcceptsNull());
    }
    private void Error(string message, TextSpan span) => context.Report("XG1016", message, span);
}
