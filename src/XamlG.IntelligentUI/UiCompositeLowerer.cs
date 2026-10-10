using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Lowers trusted response components into the same bounded tree used by all renderers.</summary>
internal sealed partial class UiCompositeLowerer(UiCatalog catalog, UiLimits limits)
{
    private int _count;
    private readonly Dictionary<string, JsonElement> _actionItems = new(StringComparer.Ordinal);
    private static readonly ImmutableDictionary<string, JsonElement> Empty = ImmutableDictionary<string, JsonElement>.Empty;
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static string Text(UiElement node, string property, string fallback = "")
        => node.Properties.TryGetValue(property, out var value) ? value.GetString()! : fallback;
    private static decimal Number(UiElement node, string property, decimal fallback)
        => node.Properties.TryGetValue(property, out var value) ? value.GetDecimal() : fallback;
    private static bool Flag(UiElement node, string property, bool fallback)
        => node.Properties.TryGetValue(property, out var value) ? value.GetBoolean() : fallback;
    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static ImmutableDictionary<string, JsonElement> Props(params (string Name, object? Value)[] values)
        => values.ToImmutableDictionary(p => p.Name, p => J(p.Value), StringComparer.Ordinal);
    private static ImmutableDictionary<string, JsonElement> Outer(UiElement node)
        => node.Properties.Where(p => UiCatalog.Default.Components["Separator"].Properties.ContainsKey(p.Key)).ToImmutableDictionary(StringComparer.Ordinal);
    private static string Key(UiElement node, string suffix) => node.Key + "/@" + suffix;

    private UiElement Make(string key, string type, ImmutableDictionary<string, JsonElement>? properties = null,
        IEnumerable<UiElement>? children = null, string? stateKey = null, string? actionId = null)
    {
        if (++_count > limits.Nodes) throw new UiException("node_limit", "Lowered response exceeds the node budget.");
        var node = new UiElement(key, type, properties ?? Empty, children?.ToImmutableArray() ?? [], stateKey, actionId)
        { ActionItem = actionId != null && _actionItems.TryGetValue(key, out var item) ? item : null };
        UiTreeValidation.ValidateElement(node, catalog.Get(type), limits.TextCharacters);
        return node;
    }
    private UiElement Label(string key, string text, int size = 14, bool bold = false)
        => Make(key, "TextBlock", Props(("Text", text), ("FontSize", size), ("FontWeight", bold ? "SemiBold" : "Normal"), ("TextWrapping", "Wrap")));
    private UiElement Stack(string key, IEnumerable<UiElement> children, decimal gap = 8,
        ImmutableDictionary<string, JsonElement>? outer = null)
        => Make(key, "StackPanel", (outer ?? Empty).SetItem("Spacing", J(gap)), children);

    internal ImmutableArray<UiElement> Run(ImmutableArray<UiElement> roots)
    {
        foreach (var node in UiSessionStore.Flatten(roots))
            if (node.ActionId != null && node.ActionItem is { } item) _actionItems.Add(node.Key, item);
        var result = roots.Select(LowerNode).ToImmutableArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Check(UiElement node, int depth)
        {
            if (depth > limits.Depth) throw new UiException("depth_limit", "Lowered response exceeds the depth budget.");
            if (!keys.Add(node.Key)) throw new UiException("duplicate_key", "Lowered response contains duplicate identities.");
            UiTreeValidation.ValidateElement(node, catalog.Get(node.Type), limits.TextCharacters);
            foreach (var child in node.Children) Check(child, depth + 1);
        }
        foreach (var root in result) Check(root, 0);
        UiStyleTree.Validate(result);
        return result;
    }
    private UiElement LowerNode(UiElement node)
    {
        if (!node.Type.StartsWith("ui:", StringComparison.Ordinal))
            return Make(node.Key, node.Type, node.Properties, node.Children.Select(LowerNode), node.StateKey, node.ActionId) with { Styles = node.Styles };
        switch (node.Type)
        {
            case "ui:Heading":
                var size = new[] { 32, 28, 24, 20, 18, 16 }[(int)Number(node, "Level", 2) - 1];
                return Make(node.Key, "TextBlock", Outer(node).SetItems(Props(("Text", Text(node, "Text")),
                    ("FontSize", size), ("FontWeight", "Bold"), ("TextWrapping", "Wrap"))));
            case "ui:Paragraph":
                return Make(node.Key, "TextBlock", Outer(node).SetItems(Props(("Text", Text(node, "Text")), ("TextWrapping", "Wrap"))));
            case "ui:Badge":
                return Make(node.Key, "Border", Outer(node).SetItems(Props(("CornerRadius", 12), ("Padding", "8,2"),
                    ("BorderThickness", 1), ("BorderBrush", "Gray"))), [Label(Key(node, "text"), Text(node, "Text"), 12, true)]);
            case "ui:Card": return Card(node);
            case "ui:Callout": return Callout(node);
            case "ui:Metric": return Metric(node);
            case "ui:KeyValue": return KeyValue(node);
            case "ui:CodeBlock": return CodeBlock(node);
            case "ui:Table": return Table(node);
            case "ui:Form": return Form(node);
            case "ui:Field": return Field(node);
            case "ui:SubmitButton": return SubmitButton(node);
            case "ui:ValidationSummary": return ValidationSummary(node);
            case "ui:BarChart": case "ui:LineChart": case "ui:ScatterChart": return Chart(node);
            default: throw new UiException("invalid_content", node.Type + " must occur inside its declared parent component.");
        }
    }
}
