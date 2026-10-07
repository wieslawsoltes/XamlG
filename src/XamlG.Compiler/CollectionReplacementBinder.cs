using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Preserves setter-before-adder alternatives, with explicit conversions taking priority.</summary>
internal sealed class CollectionReplacementBinder(BindingContext context)
{
    public bool TryBind(ObjectBindingBuilder target, BoundMember member, XamlSyntaxNode syntax, NamespaceScope scope, bool attribute = false)
    {
        if (syntax is XamlElementSyntax element && scope.Push(element).Directive(element, "Key") != null) return false;
        if (syntax is not XamlTextSyntax && context.Values.PeekValueType(syntax, scope, target.NameScopeId)?.SpecialType is not (SpecialType.System_Object or SpecialType.System_String))
        {
            if (syntax is not XamlElementSyntax intrinsic) return false;
            var name = scope.Push(intrinsic).Expand(intrinsic.Name);
            if (name.LocalName != "Null" || name.Namespace == null || !XamlNames.IsLanguage(name.Namespace)) return false;
        }
        var value = context.Values.BindNode(syntax, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        var canAssign = value is BoundConstantExpression { Value: null } ? member.ValueType.AcceptsNull() :
            value.Type != null && context.Types.Compilation.ClassifyCommonConversion(value.Type, member.ValueType).IsImplicit;
        if (canAssign && value.Type?.SpecialType != SpecialType.System_String)
        {
            value = value is BoundConstantExpression { Value: null } ? new BoundConstantExpression(null, member.ValueType, value.Span) : value;
            context.Members.AddSet(target, member, value, syntax.Span);
            return true;
        }
        if (!canAssign && value.Type?.SpecialType != SpecialType.System_Object)
        {
            var valueScope = syntax is XamlElementSyntax literal ? scope.Push(literal) : scope;
            var converted = context.Values.TryConvert(value, member.ValueType, valueScope, syntax.Span, member.ConversionSource);
            if (converted != null)
            {
                context.Members.AddSet(target, member, converted, syntax.Span);
                return true;
            }
        }
        var candidates = ImmutableArray.CreateBuilder<BoundValueSetter>();
        if (canAssign || value.Type?.SpecialType == SpecialType.System_Object) candidates.Add(new BoundPropertyValueSetter(member));
        foreach (var method in context.Types.AddMethods(member.ValueType).Where(method => method.Parameters.Length == 1))
        {
            if (!attribute && (value.Type?.SpecialType == SpecialType.System_Object || value.Type != null && context.Types.Compilation.ClassifyCommonConversion(value.Type, method.Parameters[0].Type).IsImplicit))
                candidates.Add(new BoundCollectionValueSetter(member, method));
            else if (context.Values.TryConvert(value, method.Parameters[0].Type, scope, syntax.Span) is { } converted)
            {
                target.Assignments.Add(new BoundAddAssignment(member, method, ImmutableArray.Create(converted), syntax.Span));
                return true;
            }
            else if (value is BoundConstantExpression { Value: string text } && PrimitiveValueParser.IsScalar(method.Parameters[0].Type))
            {
                context.Report("XG1008", $"Cannot convert '{text}' to '{method.Parameters[0].Type.ToDisplayString()}'.", syntax.Span);
                return true;
            }
        }
        if (value.Type?.SpecialType != SpecialType.System_Object)
        {
            if (candidates.FirstOrDefault() is BoundPropertyValueSetter)
            {
                context.Members.AddSet(target, member, value, syntax.Span);
                return true;
            }
            if (candidates.FirstOrDefault() is BoundCollectionValueSetter adder)
            {
                target.Assignments.Add(new BoundAddAssignment(member, adder.AddMethod, ImmutableArray.Create(value), syntax.Span));
                return true;
            }
            if (value is BoundConstantExpression { Value: string }) return false;
        }
        if (candidates.Count == 0) context.Report("XG1016", $"No setter or collection adder accepts the provided value for '{member.Name}'.", syntax.Span);
        else if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", syntax.Span);
        else target.Assignments.Add(new BoundDynamicSetAssignment(member, value, candidates.ToImmutable(), syntax.Span));
        return true;
    }
}
