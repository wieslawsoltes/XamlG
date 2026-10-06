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
        var dictionary = context.Types.Find(AvaloniaResourceMetadata.Dictionary);
        if (dictionary == null || !context.Types.Compilation.ClassifyCommonConversion(target.Type, dictionary).IsImplicit) return;
        var provider = context.Types.Find(AvaloniaResourceMetadata.ThemeVariantProvider);
        var key = provider?.GetMembers(AvaloniaResourceMetadata.ThemeVariantKey).OfType<IPropertySymbol>()
            .SingleOrDefault(property => property.SetMethod != null && context.Types.IsAccessible(property.SetMethod));
        // Key expressions execute once as call arguments. The same local initializes the
        // provider before its contents and is later used to insert it in ThemeDictionaries.
        for (var index = 0; index < target.Assignments.Count; index++)
            if (target.Assignments[index] is BoundAddAssignment { Arguments.Length: 2 } add &&
                add.Collection?.Name == AvaloniaResourceMetadata.ThemeDictionaries &&
                add.Arguments[1] is BoundObjectExpression value && provider != null &&
                context.Types.Compilation.ClassifyCommonConversion(value.Object.Type, provider).IsImplicit)
            {
                if (key == null) { context.Report("XG3308", "The public theme-variant provider key contract is unavailable.", add.Span); continue; }
                target.Assignments[index] = add with
                { ValueInitializers = add.ValueInitializers.Add(new BoundArgumentInitialization(key, 0)) };
            }

        bool IsMerge(BoundAssignment assignment) => assignment is BoundCallAssignment call &&
            call.Method.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Operations) && call.Method.Name == AvaloniaResourceMetadata.Merge;
        bool IsImport(BoundAssignment assignment) => IsMerge(assignment) ||
            assignment is BoundAddAssignment add && add.Collection?.Name == AvaloniaResourceMetadata.MergedDictionaries;
        var hasMerges = target.Assignments.Any(IsMerge);
        var imports = target.Assignments.Where(IsImport).ToArray();
        if (hasMerges)
        {
            var set = Method(context, AvaloniaResourceMetadata.SetResource, 3, target.Syntax.NameSpan);
            var theme = Method(context, AvaloniaResourceMetadata.MergeTheme, 3, target.Syntax.NameSpan);
            if (set == null || theme == null) return;
            for (var index = 0; index < target.Assignments.Count; index++)
            {
                if (target.Assignments[index] is not BoundAddAssignment add || add.Arguments.Length != 2) continue;
                var method = add.Collection == null ? set : add.Collection.Name == AvaloniaResourceMetadata.ThemeDictionaries ? theme : null;
                if (method != null) target.Assignments[index] = new BoundCallAssignment(method, add.Arguments, true, add.Span)
                { ValueInitializers = add.ValueInitializers };
            }
        }
        // Ordinary imports and flattened merges form ONE ordered prelude. Moving only
        // flattened merges would evaluate them before the palettes they depend on.
        foreach (var import in imports) target.Assignments.Remove(import);
        target.Assignments.InsertRange(0, imports);
    }
    private static IMethodSymbol? Method(BindingContext context, string name, int count, TextSpan span)
    {
        var method = context.Types.Find(AvaloniaResourceMetadata.Operations)?.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == count && context.Types.IsAccessible(m));
        if (method == null) context.Report("XG3308", "Resource merging requires the matching XamlG.AvaloniaRuntime package.", span);
        return method;
    }
}
