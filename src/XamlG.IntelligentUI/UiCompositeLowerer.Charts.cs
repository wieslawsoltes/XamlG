namespace XamlG.IntelligentUI;

internal sealed partial class UiCompositeLowerer
{
    private UiElement Chart(UiElement node)
    {
        var points = node.Children.Where(point => Flag(point, "IsVisible", true)).ToArray();
        if (points.Any(point => point.Type != "ui:DataPoint" || !point.Properties.ContainsKey("Value")))
            throw new UiException("invalid_chart", "Charts accept DataPoint children with a numeric Value.");
        var values = points.Select(point => Number(point, "Value", 0)).ToArray();
        var minimum = Number(node, "Minimum", values.Length == 0 ? 0 : Math.Min(0, values.Min()));
        var maximum = Number(node, "Maximum", values.Length == 0 ? 1 : Math.Max(0, values.Max()));
        if (minimum == maximum && !node.Properties.ContainsKey("Minimum") && !node.Properties.ContainsKey("Maximum")) maximum = minimum + 1;
        if (minimum >= maximum || values.Any(value => value < minimum || value > maximum))
            throw new UiException("invalid_range", "Chart values must lie within Minimum < Maximum.");
        var width = Number(node, "PlotWidth", 480); var height = Number(node, "PlotHeight", 240);
        const decimal padding = 24;
        var plotWidth = width - padding * 2; var plotHeight = height - padding * 2;
        var accent = Text(node, "Accent", "Teal");
        decimal Y(decimal value) => padding + (maximum - value) * plotHeight / (maximum - minimum);
        var xs = points.Select((point, index) => Number(point, "X", index)).ToArray();
        var xmin = xs.Length == 0 ? 0 : xs.Min(); var xmax = xs.Length == 0 ? 1 : xs.Max();
        decimal X(int index) => node.Type == "ui:BarChart" ? padding + (index + .5m) * plotWidth / Math.Max(1, points.Length)
            : xmin == xmax ? width / 2 : padding + (xs[index] - xmin) * plotWidth / (xmax - xmin);
        var graphics = new List<UiElement>();
        var baseline = Math.Clamp(0m, minimum, maximum);
        graphics.Add(Line(Key(node, "baseline"), padding, Y(baseline), width - padding, Y(baseline), "Gray", width, height));
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index]; var x = X(index); var y = Y(values[index]);
            var label = Text(point, "Label", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var description = label + ": " + Format(values[index]);
            if (node.Type == "ui:BarChart")
            {
                var barWidth = Math.Max(1, plotWidth / Math.Max(1, points.Length) * .7m);
                graphics.Add(Make(point.Key, "Rectangle", Props(("Width", barWidth), ("Height", Math.Abs(Y(baseline) - y)),
                    ("Canvas.Left", x - barWidth / 2), ("Canvas.Top", Math.Min(y, Y(baseline))), ("Fill", accent),
                    ("ToolTip.Tip", description), ("AutomationProperties.Name", description))));
            }
            else
            {
                if (node.Type == "ui:LineChart" && index > 0)
                    graphics.Add(Line(Key(point, "segment"), X(index - 1), Y(values[index - 1]), x, y, accent, width, height));
                graphics.Add(Make(point.Key, "Ellipse", Props(("Width", 6), ("Height", 6), ("Canvas.Left", x - 3),
                    ("Canvas.Top", y - 3), ("Fill", accent), ("ToolTip.Tip", description), ("AutomationProperties.Name", description))));
            }
        }
        var canvas = Make(Key(node, "plot"), "Canvas", Props(("Width", width), ("Height", height), ("ClipToBounds", true)), graphics);
        var scaled = Make(Key(node, "scale"), "Viewbox", Props(("Stretch", "Uniform"), ("StretchDirection", "DownOnly")), [canvas]);
        var body = new List<UiElement>();
        if (Text(node, "Title") is { Length: > 0 } title) body.Add(Label(Key(node, "title"), title, 18, true));
        body.Add(scaled);
        body.Add(Label(Key(node, "range"), "Range: " + Format(minimum) + " – " + Format(maximum), 12));
        if (Flag(node, "ShowLegend", true))
            for (var index = 0; index < points.Length; index++)
                body.Add(Label(Key(points[index], "legend"), Text(points[index], "Label", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ": " + Format(values[index]), 12));
        if (points.Length == 0) body.Add(Label(Key(node, "empty"), "No data."));
        return Stack(node.Key, body, 4, Outer(node));
    }
    private UiElement Line(string key, decimal x1, decimal y1, decimal x2, decimal y2, string color, decimal width, decimal height)
        => Make(key, "Line", Props(("Width", width), ("Height", height), ("StartPoint", Format(x1) + "," + Format(y1)),
            ("EndPoint", Format(x2) + "," + Format(y2)), ("Stroke", color), ("StrokeThickness", 2)));
}
