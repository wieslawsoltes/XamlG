namespace XamlG.IntelligentUI;

internal sealed partial class UiCompositeLowerer
{
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
