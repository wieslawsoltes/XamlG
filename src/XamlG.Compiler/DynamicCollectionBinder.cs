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
        NamespaceScope scope, BoundExpression? keyValue, IMethodSymbol[] methods, bool attribute = false)
    {
        if (methods.Length == 0) return false;
        if (syntax is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return false;
        }
        else if (context.Values.PeekValueType(syntax, scope, target.NameScopeId)?.SpecialType != SpecialType.System_Object) return false;

        var value = context.Values.BindNode(syntax, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        var candidates = ImmutableArray.CreateBuilder<IMethodSymbol>();
        foreach (var method in methods)
        {
            var type = method.Parameters.Last().Type;
            var compatible = !attribute && (value is BoundConstantExpression { Value: null } ? type.AcceptsNull() :
                value.Type?.SpecialType == SpecialType.System_Object ? context.Types.Compilation.ClassifyCommonConversion(value.Type, type).Exists : CanAssign(value, type));
            if (compatible)
            {
                if (!candidates.Any(previous => Subsumes(previous.Parameters.Last().Type, type))) candidates.Add(method);
            }
            else if (context.Values.TryConvert(value, type, scope, syntax.Span) is { } converted)
            {
                target.Assignments.Add(new BoundAddAssignment(member, method,
                    keyValue == null ? ImmutableArray.Create(converted) : ImmutableArray.Create(keyValue, converted), syntax.Span));
                return true;
            }
        }
        if (candidates.Count == 0) { context.Report(attribute ? "XG1010" : "XG1016", "No compatible collection overload accepts the provided value.", syntax.Span); return true; }
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
}
