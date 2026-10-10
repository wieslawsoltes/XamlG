using System.Collections.Immutable;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    private static void ValidateActionReferences(ImmutableArray<UiElement> roots, ImmutableArray<UiAction> actions)
    {
        var ids = actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (Flatten(roots).Any(node => node.ActionId != null && !ids.Contains(node.ActionId))) throw new UiException("invalid_action", "A node references an undeclared action.");
    }
    public static IEnumerable<UiElement> Flatten(IEnumerable<UiElement> nodes)
    {
        var stack = new Stack<(UiElement Node, int Depth)>(nodes.Reverse().Select(node => (node, 0))); var count = 0;
        while (stack.TryPop(out var item))
        {
            if (item.Node == null || item.Depth > 48 || ++count > 4096 || item.Node.Children.IsDefault) throw new UiException("invalid_tree", "Invalid or oversized tree.");
            yield return item.Node;
            foreach (var child in item.Node.Children.Reverse()) stack.Push((child, item.Depth + 1));
        }
    }
    private static bool Disabled(UiElement target, IEnumerable<UiElement> roots) => Unavailable(target, roots, includeEnabled: true);
    private static bool Unavailable(UiElement target, IEnumerable<UiElement> roots, bool includeEnabled)
    {
        foreach (var node in roots)
        {
            if (node.Key != target.Key && !Flatten(node.Children).Any(child => child.Key == target.Key)) continue;
            if (node.Properties.TryGetValue("IsVisible", out var visible) && !visible.GetBoolean() || includeEnabled && node.Properties.TryGetValue("IsEnabled", out var enabled) && !enabled.GetBoolean()) return true;
            if (node.Key == target.Key) return false;
            if (node.Type is "Expander" or "TreeViewItem" && (!node.Properties.TryGetValue("IsExpanded", out var expanded) || !expanded.GetBoolean())) return true;
            if (node.Type == "TabControl")
            {
                var selected = node.Properties.TryGetValue("SelectedIndex", out var selection) ? (int)selection.GetDecimal() : 0;
                if (selected < 0 || selected >= node.Children.Length || !Flatten([node.Children[selected]]).Any(child => child.Key == target.Key)) return true;
                return Unavailable(target, [node.Children[selected]], includeEnabled);
            }
            return Unavailable(target, node.Children, includeEnabled);
        }
        return true;
    }
    private string Fallback(ImmutableArray<UiElement> roots, string? markdown)
    {
        var result = new System.Text.StringBuilder();
        void Add(string text)
        {
            var remaining = Compiler.Limits.TextCharacters - result.Length;
            if (remaining <= 0 || text.Length == 0) return;
            if (result.Length > 0) { result.Append('\n'); remaining--; }
            if (remaining > 0) result.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
        }
        if (!string.IsNullOrWhiteSpace(markdown)) Add(markdown);
        foreach (var node in Flatten(roots))
        {
            if (Unavailable(node, roots, includeEnabled: false)) continue;
            foreach (var field in node.Properties.Where(p => p.Key is "Text" or "Content" or "Header" or "Value" or "IsChecked" or "SelectedDate" or "SelectedTime")) Add(UiJson.Text(UiJson.Value(field.Value)));
            if (node.Properties.TryGetValue("ItemsSource", out var items)) foreach (var item in items.EnumerateArray()) Add(item.GetString() ?? "");
        }
        return result.ToString();
    }
}
