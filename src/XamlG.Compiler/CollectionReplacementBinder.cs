using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Preserves setter-before-adder alternatives for dynamically provided first values.</summary>
internal sealed class CollectionReplacementBinder(BindingContext context)
{
    public bool TryBind(ObjectBindingBuilder target, BoundMember member, XamlSyntaxNode syntax, NamespaceScope scope)
    {
        if (syntax is XamlElementSyntax element && scope.Push(element).Directive(element, "Key") != null) return false;
        if (syntax is not XamlTextSyntax && context.Values.PeekValueType(syntax, scope)?.SpecialType is not (SpecialType.System_Object or SpecialType.System_String))
        {
            if (syntax is not XamlElementSyntax intrinsic) return false;
            var name = scope.Push(intrinsic).Expand(intrinsic.Name);
            if (name.LocalName != "Null" || name.Namespace == null || !XamlNames.IsLanguage(name.Namespace)) return false;
        }
        var value = context.Values.BindNode(syntax, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        if (value is BoundConstantExpression { Value: null } ? member.ValueType.AcceptsNull() :
            value.Type != null && context.Types.Compilation.ClassifyCommonConversion(value.Type, member.ValueType).IsImplicit)
        {
            value = value is BoundConstantExpression { Value: null } ? new BoundConstantExpression(null, member.ValueType, value.Span) : value;
            context.Members.AddSet(target, member, value, syntax.Span);
            return true;
        }
        if (value is BoundConstantExpression { Value: string text })
        {
            var valueScope = syntax is XamlElementSyntax literal ? scope.Push(literal) : scope;
            var converted = context.Values.TryText(text, member.ValueType, valueScope, syntax.Span, member.ConversionSource);
            if (converted == null) return false;
            context.Members.AddSet(target, member, converted, syntax.Span);
            return true;
        }
        var candidates = ImmutableArray.CreateBuilder<BoundValueSetter>();
        if (value.Type?.SpecialType == SpecialType.System_Object) candidates.Add(new BoundPropertyValueSetter(member));
        foreach (var method in context.Types.AddMethods(member.ValueType).Where(method => method.Parameters.Length == 1))
            if (value.Type?.SpecialType == SpecialType.System_Object || value.Type != null && context.Types.Compilation.ClassifyCommonConversion(value.Type, method.Parameters[0].Type).IsImplicit)
                candidates.Add(new BoundCollectionValueSetter(member, method));
        if (value.Type?.SpecialType != SpecialType.System_Object && candidates.FirstOrDefault() is BoundCollectionValueSetter adder)
        {
            target.Assignments.Add(new BoundAddAssignment(member, adder.AddMethod, ImmutableArray.Create(value), syntax.Span));
            return true;
        }
        if (candidates.Count == 0) context.Report("XG1016", $"No setter or collection adder accepts the provided value for '{member.Name}'.", syntax.Span);
        else if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", syntax.Span);
        else target.Assignments.Add(new BoundDynamicSetAssignment(member, value, candidates.ToImmutable(), syntax.Span));
        return true;
    }
}
