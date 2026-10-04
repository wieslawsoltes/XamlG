using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Resources;

/// <summary>Schedules eager compiled merges before local declarations and preserves local resource/theme precedence.</summary>
public sealed class AvaloniaResourceMergeRule : IXamlPropertyBindingRule, IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (!target.Type.HasMetadataName(AvaloniaResourceMetadata.Dictionary) || member.Name != AvaloniaResourceMetadata.MergedDictionaries) return false;
        bool IsMerge(XamlSyntaxNode value) => value is XamlElementSyntax node && context.Values.PeekNodeType(node, scope)?.HasMetadataName(AvaloniaResourceMetadata.MergeResourceInclude) == true;
        if (!values.Any(IsMerge)) return false;
        var merge = Method(context, AvaloniaResourceMetadata.Merge, 2, span);
        if (merge == null) return true;
        var seenMerge = false;
        foreach (var value in values)
        {
            if (!IsMerge(value))
            {
                if (seenMerge) { context.Report("XG3307", "MergeResourceInclude must follow other entries in MergedDictionaries.", value.Span); continue; }
                context.Members.BindNodes(target, member, new[] { value }, scope, value.Span);
                continue;
            }
            seenMerge = true;
            var resource = AvaloniaResourceIncludeRule.Resolve(context, (XamlElementSyntax)value, scope, false);
            if (resource != null) target.Assignments.Add(new BoundCallAssignment(merge, ImmutableArray.Create<BoundExpression>(resource), true, value.Span));
        }
        return true;
    }
    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (!target.Type.HasMetadataName(AvaloniaResourceMetadata.Dictionary)) return;
        var merges = target.Assignments.OfType<BoundCallAssignment>().Where(a => a.Method.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Operations) && a.Method.Name == AvaloniaResourceMetadata.Merge).ToArray();
        if (merges.Length == 0) return;
        var set = Method(context, AvaloniaResourceMetadata.SetResource, 3, target.Syntax.NameSpan);
        var theme = Method(context, AvaloniaResourceMetadata.MergeTheme, 3, target.Syntax.NameSpan);
        if (set == null || theme == null) return;
        foreach (var merge in merges) target.Assignments.Remove(merge);
        for (var i = 0; i < target.Assignments.Count; i++)
        {
            if (target.Assignments[i] is not BoundAddAssignment add || add.Arguments.Length != 2) continue;
            if (add.Collection == null) target.Assignments[i] = new BoundCallAssignment(set, add.Arguments, true, add.Span);
            else if (add.Collection.Name == AvaloniaResourceMetadata.ThemeDictionaries)
                target.Assignments[i] = new BoundCallAssignment(theme, add.Arguments, true, add.Span);
        }
        target.Assignments.InsertRange(0, merges);
    }
    private static IMethodSymbol? Method(BindingContext context, string name, int count, TextSpan span)
    {
        var method = context.Types.Find(AvaloniaResourceMetadata.Operations)?.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == count && context.Types.IsAccessible(m));
        if (method == null) context.Report("XG3308", "Resource merging requires the matching XamlG.AvaloniaRuntime package.", span);
        return method;
    }
}
