using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

public sealed class AvaloniaBindingRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (!member.CanWrite || member.TargetDescriptor == null || values.Length != 1 ||
            member.Symbol.HasAttribute(new[] { AvaloniaMetadata.AssignBinding })) return false;
        var markup = values[0] is XamlTextSyntax text && text.Value.StartsWith("{", StringComparison.Ordinal) && !text.Value.StartsWith("{}", StringComparison.Ordinal);
        var nodeType = markup ? null : context.Values.PeekNodeType(values[0], scope);
        var bindingType = context.Types.Find(AvaloniaMetadata.BindingBase);
        if (!markup && (bindingType == null || nodeType == null || !context.Types.Compilation.ClassifyCommonConversion(nodeType, bindingType).IsImplicit)) return false;
        var adapter = context.Types.Find(AvaloniaMetadata.BindingAdapter)?.Members(AvaloniaMetadata.ApplyBinding).OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 3 && context.Types.IsAccessible(m));
        if (adapter == null || bindingType == null)
        {
            context.Report("XG3002", "Avalonia binding compilation requires a reference to XamlG.AvaloniaRuntime.", span);
            return true;
        }
        using var expected = new AvaloniaBindingTargetScope(target, member.ValueType);
        BoundExpression? value;
        if (values[0] is XamlElementSyntax element && new AvaloniaCompiledBindingRule().TryBindElement(context, element, member.ValueType, scope, out var compiled))
            value = compiled;
        else
            value = context.Values.BindNode(values[0], context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId, normalizeText: false);
        if (value == null) return true;
        if (!target.AssignedScalars.Add(member.Symbol.ToDisplayString()))
        {
            context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span);
            return true;
        }
        target.Assignments.Add(new BoundAdaptedSetAssignment(member, value, ImmutableArray.Create<ITypeSymbol>(bindingType), adapter, span)
        { OwnAdapterResult = true });
        return true;
    }
}
