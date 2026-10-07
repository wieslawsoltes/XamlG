using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Assigns through a public Avalonia registration when its CLR wrapper cannot
/// be written. Private setters need not be imported. Runtime registration semantics
/// still reject read-only writes; unregistered CLR properties retain normal diagnostics.</summary>
public sealed class AvaloniaRegisteredSetterRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (member.CanWrite || member.TargetDescriptor == null || values.Length != 1 ||
            member.Kind != BoundMemberKind.Property || member.Symbol is not IPropertySymbol { IsStatic: false, IsIndexer: false }) return false;
        var objectType = context.Types.Find(AvaloniaMetadata.Object);
        if (objectType == null || !context.Types.Compilation.ClassifyCommonConversion(target.Type, objectType).IsImplicit) return false;
        var adapter = AvaloniaRegisteredValue.Adapter(context);
        if (adapter == null)
        {
            context.Report("XG3002", "Registered-property assignment requires the matching XamlG.AvaloniaRuntime contract.", span);
            return true;
        }
        var markup = values[0] is XamlTextSyntax text && text.Value.StartsWith("{", StringComparison.Ordinal) && !text.Value.StartsWith("{}", StringComparison.Ordinal);
        var binding = context.Types.Find(AvaloniaMetadata.BindingBase);
        var nodeType = markup ? null : context.Values.PeekNodeType(values[0], scope);
        var bindingObject = binding != null && nodeType != null && context.Types.Compilation.ClassifyCommonConversion(nodeType, binding).IsImplicit;
        using var expected = new AvaloniaBindingTargetScope(target, member.ValueType);
        BoundExpression? value;
        if (values[0] is XamlElementSyntax element && new AvaloniaCompiledBindingRule().TryBindElement(context, element, member.ValueType, scope, out var compiled))
            value = compiled;
        else value = context.Values.BindNode(values[0], markup || bindingObject
            ? context.Types.Special(SpecialType.System_Object) : member.ValueType, scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        target.Assignments.Add(new BoundCallAssignment(adapter,
            ImmutableArray.Create(member.TargetDescriptor, value), true, span)
            { OwnResult = true, TargetDescriptor = member.TargetDescriptor });
        return true;
    }
}
