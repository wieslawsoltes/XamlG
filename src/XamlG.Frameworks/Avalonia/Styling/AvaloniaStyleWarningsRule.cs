using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Preserves the pinned compiler's warning severity and source-level scope.</summary>
public sealed class AvaloniaStyleWarningsRule : IXamlObjectBindingRule, IXamlPropertyBindingRule
{
    private static readonly XamlAnnotationKey<HashSet<TextSpan>> Reported = new("Avalonia.StyleWarnings");

    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        if (!AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Style) && !AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.ControlTheme)) return;
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setter in target.Syntax.Children.OfType<XamlElementSyntax>())
        {
            if (VisibleType(context, setter, target.Scope)?.Name != "Setter") continue;
            var source = AvaloniaStyleObjectRule.TextMember(setter, AvaloniaStyleMetadata.PropertyMember);
            if (source is not { } property || property.Text.StartsWith("{", StringComparison.Ordinal)) continue;
            if (!properties.Add(property.Text))
                context.Report("XG3107", "Duplicate setter for property '" + property.Text + "'.", property.Span, XamlSeverity.Warning);
        }
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target) { }

    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute)
    {
        if (member.Name == AvaloniaResourceMetadata.MergedDictionaries && member.Symbol.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Dictionary))
            foreach (var node in values.OfType<XamlElementSyntax>())
                if (VisibleType(context, node, scope) is { } type && AvaloniaStyleScope.Is(type, AvaloniaResourceMetadata.Style) && !HasTargetScope(type, node, scope))
                    Report(context, target, "XG3309", "Including " + type.Name + " in MergedDictionaries ignores its nested styles. Add it to the Styles collection instead.", node.NameSpan);

        if ((member.Name == "ItemTemplate" && member.Symbol.ContainingType.HasMetadataName("Avalonia.Controls.ItemsControl") ||
             member.Name == "DataTemplates" && member.Symbol.ContainingType.HasMetadataName(AvaloniaMetadata.Control)) &&
            ContainerName(target.Type) is { } containerName && context.Types.Find(containerName) is { } container)
            foreach (var node in values.OfType<XamlElementSyntax>())
                CheckTemplate(context, target, member.Name, node, scope, container);
        return false;
    }

    private static void CheckTemplate(BindingContext context, ObjectBindingBuilder owner, string member, XamlElementSyntax node, NamespaceScope scope, INamedTypeSymbol container)
    {
        if (node.LocalName.IndexOf('.') >= 0 || VisibleType(context, node, scope) is not { } type) return;
        var nested = scope.Push(node);
        foreach (var child in node.Children.OfType<XamlElementSyntax>())
        {
            if (AvaloniaStyleScope.Is(type, "Avalonia.Controls.Templates.IDataTemplate") && VisibleType(context, child, nested) is { } childType &&
                AvaloniaStyleScope.Is(childType, "Avalonia.Controls.ContentControl") && AvaloniaStyleScope.Is(childType, container.MetadataName()) && !HasTargetScope(childType, child, nested))
                Report(context, owner, "XG3117", "Unexpected '" + container.Name + "' inside '" + owner.Type.Name + "." + member + "'. The template defines container content, not the container itself.", child.NameSpan);
            CheckTemplate(context, owner, member, child, nested, container);
        }
    }

    // Upstream checks the immediate AST parent. A target-type metadata wrapper
    // interrupts that relationship, so these nodes do not receive the warning.
    private static bool HasTargetScope(ITypeSymbol type, XamlElementSyntax node, NamespaceScope scope) =>
        scope.Push(node).Directive(node, AvaloniaStyleMetadata.SetterTargetType) != null ||
        AvaloniaStyleScope.Is(type, AvaloniaStyleMetadata.ControlTheme) || AvaloniaStyleScope.IsTemplate(type) ||
        AvaloniaStyleScope.Is(type, AvaloniaStyleMetadata.Style) && !string.IsNullOrWhiteSpace(AvaloniaStyleObjectRule.TextMember(node, AvaloniaStyleMetadata.SelectorMember)?.Text);

    private static ITypeSymbol? VisibleType(BindingContext context, XamlElementSyntax node, NamespaceScope scope)
    {
        if (node.LocalName.IndexOf('.') >= 0) return null;
        var nested = scope.Push(node);
        var ns = nested.Expand(node.Name).Namespace;
        return ns != null && (nested.IgnoredNamespaces.Contains(ns) || context.Types.Configuration.IgnoredNamespaces.Contains(ns)) ? null : context.Values.PeekNodeType(node, scope);
    }

    private static void Report(BindingContext context, ObjectBindingBuilder target, string code, string message, TextSpan span)
    {
        if (!target.Annotations.TryGet(Reported, out var spans)) target.Annotations.Set(Reported, spans = new());
        if (spans.Add(span)) context.Report(code, message, span, XamlSeverity.Warning);
    }

    private static string? ContainerName(ITypeSymbol type) => type.MetadataName() switch
    {
        "Avalonia.Controls.ListBox" => "Avalonia.Controls.ListBoxItem",
        "Avalonia.Controls.ComboBox" => "Avalonia.Controls.ComboBoxItem",
        "Avalonia.Controls.Menu" or "Avalonia.Controls.MenuItem" => "Avalonia.Controls.MenuItem",
        "Avalonia.Controls.Primitives.TabStrip" => "Avalonia.Controls.Primitives.TabStripItem",
        "Avalonia.Controls.TabControl" => "Avalonia.Controls.TabItem",
        "Avalonia.Controls.TreeView" => "Avalonia.Controls.TreeViewItem",
        _ => null
    };
}
