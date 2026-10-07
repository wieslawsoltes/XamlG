using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

public sealed class AvaloniaBindingRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (!member.CanWrite || member.TargetDescriptor == null || values.Length != 1) return false;
        var providedType = context.Values.PeekValueType(values[0], scope, target.NameScopeId);
        if (providedType == null) return false;
        var bindingType = context.Types.Find(AvaloniaMetadata.BindingBase);
        var unsetType = context.Types.Find(AvaloniaRegisteredSetterMetadata.UnsetValueType);
        var assignBinding = member.Symbol.HasAttribute(new[] { AvaloniaMetadata.AssignBinding });
        var isUnset = providedType.HasMetadataName(AvaloniaRegisteredSetterMetadata.UnsetValueType);
        var isBinding = !assignBinding && bindingType != null && context.Types.Compilation.ClassifyCommonConversion(providedType, bindingType).IsImplicit;
        if (!isUnset && !isBinding && providedType.SpecialType != SpecialType.System_Object) return false;
        // A known value's conversion selects the CLR setter before runtime alternatives.
        if (providedType.SpecialType != SpecialType.System_Object &&
            !context.Types.Compilation.ClassifyCommonConversion(providedType, member.ValueType).IsImplicit &&
            context.Values.CanConvertValueType(providedType, member.ValueType, member.ConversionSource)) return false;
        var staticUnset = isUnset && (member.StaticSetter == null ||
            !context.Types.Compilation.ClassifyCommonConversion(providedType, member.ValueType).IsImplicit);
        var alternatives = ImmutableArray.CreateBuilder<ITypeSymbol>();
        // Template-priority setters replace the entire candidate set, dropping UnsetValue.
        if (member.StaticSetter == null && unsetType != null) alternatives.Add(unsetType);
        if (!assignBinding && bindingType != null) alternatives.Add(bindingType);
        if (!staticUnset && alternatives.Count == 0) return false;
        var adapter = AvaloniaRegisteredValue.Adapter(context);
        if (adapter == null || unsetType == null || bindingType == null)
        {
            context.Report("XG3002", "Avalonia binding compilation requires a reference to XamlG.AvaloniaRuntime.", span);
            return true;
        }
        var value = AvaloniaRegisteredValue.Bind(context, target, member, values[0], scope, context.Types.Special(SpecialType.System_Object));
        if (value == null) return true;
        // Upstream validates a statically typed unset provider but never evaluates it.
        if (staticUnset && (value = AvaloniaRegisteredValue.Unset(context, span)) == null) return true;
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        if (staticUnset)
            target.Assignments.Add(new BoundCallAssignment(adapter, ImmutableArray.Create(member.TargetDescriptor, value), true, span)
                { TargetDescriptor = member.TargetDescriptor });
        else
            target.Assignments.Add(new BoundAdaptedSetAssignment(member, value, alternatives.ToImmutable(), adapter, span)
                { OwnAdapterResult = true });
        return true;
    }
}
