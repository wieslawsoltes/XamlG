using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Lowers trusted response components into the same bounded tree used by all renderers.</summary>
internal sealed partial class UiCompositeLowerer(UiCatalog catalog, UiLimits limits)
{
    private int _count;
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
        var node = new UiElement(key, type, properties ?? Empty, children?.ToImmutableArray() ?? [], stateKey, actionId);
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
        return result;
    }
    private UiElement LowerNode(UiElement node)
    {
        if (!node.Type.StartsWith("ui:", StringComparison.Ordinal))
            return Make(node.Key, node.Type, node.Properties, node.Children.Select(LowerNode), node.StateKey, node.ActionId);
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
            case "ui:BarChart": case "ui:LineChart": case "ui:ScatterChart": return Chart(node);
            default: throw new UiException("invalid_content", node.Type + " must occur inside its declared parent component.");
        }
    }
    private UiElement Card(UiElement node)
    {
        var children = new List<UiElement>();
        if (Text(node, "Title") is { Length: > 0 } title) children.Add(Label(Key(node, "title"), title, 20, true));
        children.AddRange(node.Children.Select(LowerNode));
        var body = Stack(Key(node, "body"), children, Number(node, "Gap", 2) * 4);
        return Make(node.Key, "Border", Outer(node).SetItems(Props(("Padding", Number(node, "Space", 3) * 4),
            ("CornerRadius", Number(node, "Radius", 2) * 4), ("BorderThickness", 1), ("BorderBrush", "Gray"))), [body]);
    }
    private UiElement Callout(UiElement node)
    {
        var children = new List<UiElement>();
        if (Text(node, "Title") is { Length: > 0 } title) children.Add(Label(Key(node, "title"), title, 16, true));
        children.Add(Label(Key(node, "text"), Text(node, "Text")));
        var brush = Text(node, "Tone", "Info") switch { "Success" => "Green", "Warning" => "Orange", "Error" => "Red", _ => "Teal" };
        var body = Stack(Key(node, "body"), children, 4);
        return Make(node.Key, "Border", Outer(node).SetItems(Props(("Padding", 12), ("BorderThickness", "4,0,0,0"), ("BorderBrush", brush))), [body]);
    }
    private UiElement Metric(UiElement node)
    {
        var children = new List<UiElement>();
        if (Text(node, "Title") is { Length: > 0 } title) children.Add(Label(Key(node, "title"), title, 14, true));
        children.Add(Label(Key(node, "value"), Text(node, "Value"), 32, true));
        if (Text(node, "Detail") is { Length: > 0 } detail) children.Add(Label(Key(node, "detail"), detail, 12));
        return Stack(node.Key, children, 4, Outer(node));
    }
    private UiElement KeyValue(UiElement node)
    {
        var title = Label(Key(node, "title"), Text(node, "Title"), 14, true);
        var value = Label(Key(node, "value"), Text(node, "Value"));
        value = value with { Properties = value.Properties.SetItem("Grid.Column", J(1)).SetItem("Margin", J("12,0,0,0")) };
        return Make(node.Key, "Grid", Outer(node).SetItem("ColumnDefinitions", J("Auto,*")), [title, value]);
    }
    private UiElement CodeBlock(UiElement node)
    {
        var children = new List<UiElement>();
        if (Text(node, "Language") is { Length: > 0 } language) children.Add(Label(Key(node, "language"), language, 12, true));
        var source = Make(Key(node, "source"), "SelectableTextBlock", Props(("Text", Text(node, "Text")), ("TextWrapping", "NoWrap")));
        children.Add(Make(Key(node, "scroll"), "ScrollViewer", Props(("MaxHeight", Number(node, "ViewportHeight", 240))), [source]));
        var body = Stack(Key(node, "body"), children, 4);
        return Make(node.Key, "Border", Outer(node).SetItems(Props(("Padding", 12), ("CornerRadius", 4), ("BorderThickness", 1), ("BorderBrush", "Gray"))), [body]);
    }
    private UiElement Table(UiElement node)
    {
        var columns = Text(node, "Columns").Split(',').Select(column => column.Trim()).ToArray();
        if (columns.Length is < 1 or > 16 || columns.Any(column => column.Length is 0 or > 256))
            throw new UiException("invalid_table", "Table Columns requires 1–16 nonempty comma-separated headings.");
        var definitions = Text(node, "ColumnDefinitions", string.Join(',', Enumerable.Repeat("*", columns.Length)));
        if (definitions.Split(',').Length != columns.Length) throw new UiException("invalid_table", "Table track count must match the headings.");
        var rows = new List<UiElement>();
        var headers = columns.Select((column, index) => Cell(node, "header-" + index, index,
            Label(Key(node, "heading-" + index), column, 14, true))).ToArray();
        rows.Add(Make(Key(node, "header"), "Grid", Props(("ColumnDefinitions", definitions)), headers));
        foreach (var row in node.Children)
        {
            if (row.Type != "ui:TableRow" || row.Children.Length != columns.Length)
                throw new UiException("invalid_table", "Every TableRow must contain one cell per column.");
            var cells = row.Children.Select((child, index) => Cell(row, "cell-" + index, index, LowerNode(child))).ToArray();
            rows.Add(Make(row.Key, "Grid", Outer(row).SetItem("ColumnDefinitions", J(definitions)), cells));
        }
        if (node.Children.IsEmpty) rows.Add(Label(Key(node, "empty"), Text(node, "EmptyText", "No rows.")));
        return Stack(node.Key, rows, 0, Outer(node));
    }
    private UiElement Cell(UiElement owner, string suffix, int column, UiElement content)
        => Make(Key(owner, suffix), "Border", Props(("Grid.Column", column), ("Padding", 8),
            ("BorderThickness", "0,0,0,1"), ("BorderBrush", "Gray")), [content]);
}
