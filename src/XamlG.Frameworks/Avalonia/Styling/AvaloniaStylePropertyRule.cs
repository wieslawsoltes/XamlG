using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaStylePropertyRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (target.Type.HasMetadataName(AvaloniaStyleMetadata.Style) && member.Name == AvaloniaStyleMetadata.SelectorMember)
        {
            if (target.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector))
                context.Members.AddSet(target, member, selector.Expression, span);
            return true;
        }
        if (member.Name == AvaloniaStyleMetadata.ClassesMember && member.ValueType.HasMetadataName(AvaloniaStyleMetadata.Classes) && values.All(v => v is XamlTextSyntax))
        {
            var add = context.Types.AddMethods(member.ValueType).FirstOrDefault(m => m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
            if (add == null) { context.Report("XG3104", "The Classes.Add contract is unavailable.", span); return true; }
            foreach (var item in values.Cast<XamlTextSyntax>())
                foreach (var name in item.Value.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
                    target.Assignments.Add(new BoundAddAssignment(member, add, ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(name, add.Parameters[0].Type, item.Span)), item.Span));
            return true;
        }
        if (!target.Type.HasMetadataName(AvaloniaStyleMetadata.Setter)) return false;
        if (member.Name == AvaloniaStyleMetadata.PropertyMember)
        {
            if (target.Annotations.TryGet(AvaloniaStyleAnnotations.SetterProperty, out var property))
                context.Members.AddSet(target, member, property.Reference(span), span);
            return true;
        }
        if (member.Name != AvaloniaStyleMetadata.ValueMember) return false;
        if (!target.Annotations.TryGet(AvaloniaStyleAnnotations.SetterProperty, out var registered))
        { context.Report("XG3102", "Setter.Value requires a statically resolved Setter.Property.", span); return true; }
        if (values.Length != 1) { context.Report("XG3108", "A setter requires exactly one value.", span); return true; }
        using var expected = new AvaloniaBindingTargetScope(target, registered.ValueType);
        var node = values[0];
        BoundExpression? value;
        if (node is XamlTextSyntax text && (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)))
            value = context.Values.BindText(text.Value, registered.ValueType, scope, text.Span, registered.Field);
        else
        {
            if (node is XamlElementSyntax bindingElement && new AvaloniaCompiledBindingRule().TryBindElement(context, bindingElement, registered.ValueType, scope, out var compiled)) value = compiled;
            else value = context.Values.BindNode(node, context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
            if (value?.Type != null && value.Type.SpecialType != SpecialType.System_Object && !IsSpecialValue(context, value.Type))
                value = context.Values.Coerce(value, registered.ValueType, span);
        }
        if (value != null) context.Members.AddSet(target, member with { TargetDescriptor = registered.Reference(span) }, value, span);
        return true;
    }

    private static bool IsSpecialValue(BindingContext context, ITypeSymbol type)
    {
        var binding = context.Types.Find(AvaloniaMetadata.BindingBase);
        var template = context.Types.Find(AvaloniaStyleMetadata.Template);
        return binding != null && context.Types.Compilation.ClassifyCommonConversion(type, binding).IsImplicit ||
               template != null && context.Types.Compilation.ClassifyCommonConversion(type, template).IsImplicit;
    }
}
