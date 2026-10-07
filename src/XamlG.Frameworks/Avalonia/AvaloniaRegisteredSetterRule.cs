using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
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
        var providedType = context.Values.PeekValueType(values[0], scope, target.NameScopeId);
        var bindingType = context.Types.Find(AvaloniaMetadata.BindingBase);
        var assignBinding = member.Symbol.HasAttribute(new[] { AvaloniaMetadata.AssignBinding });
        var dynamicValue = providedType?.SpecialType == SpecialType.System_Object;
        var staticUnset = providedType?.HasMetadataName(AvaloniaRegisteredSetterMetadata.UnsetValueType) == true || dynamicValue && assignBinding;
        var providedBinding = !assignBinding && providedType != null && bindingType != null &&
            context.Types.Compilation.ClassifyCommonConversion(providedType, bindingType).IsImplicit;
        var bindingOnly = !staticUnset && (providedBinding || dynamicValue && AvaloniaTemplatePriority.HasPrioritySetter(context, member));
        var adapter = AvaloniaRegisteredValue.Adapter(context, bindingOnly ? AvaloniaRegisteredSetterMetadata.AssignBinding :
            dynamicValue && !staticUnset ? AvaloniaRegisteredSetterMetadata.AssignBindingOrUnset : AvaloniaRegisteredSetterMetadata.AssignValue);
        if (adapter == null)
        {
            context.Report("XG3002", "Registered-property assignment requires the matching XamlG.AvaloniaRuntime contract.", span);
            return true;
        }
        var value = AvaloniaRegisteredValue.Bind(context, target, member, values[0], scope,
            staticUnset || dynamicValue || providedBinding ? context.Types.Special(SpecialType.System_Object) : member.ValueType);
        if (value == null) return true;
        if (staticUnset) value = AvaloniaRegisteredValue.Unset(context, span);
        if (value == null) return true;
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        target.Assignments.Add(new BoundCallAssignment(adapter,
            ImmutableArray.Create(member.TargetDescriptor, value), true, span)
            { OwnResult = !adapter.ReturnsVoid, TargetDescriptor = member.TargetDescriptor });
        return true;
    }
}
