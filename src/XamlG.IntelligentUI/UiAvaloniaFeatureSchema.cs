using System.Collections.Immutable;

namespace XamlG.IntelligentUI;

/// <summary>Additional public Avalonia capabilities. Literal grammars are checked by
/// UiDrawingValues before either native mutation or publication of a resolved tree.</summary>
internal static class UiAvaloniaFeatureSchema
{
    internal static IEnumerable<UiComponent> Extend(IEnumerable<UiComponent> source)
    {
        var components = source.ToDictionary(component => component.Name, StringComparer.Ordinal);
        var text = new UiProperty(UiPropertyKind.Text);
        var boolean = new UiProperty(UiPropertyKind.Boolean);
        var color = new UiProperty(UiPropertyKind.Color);
        var thickness = new UiProperty(UiPropertyKind.Thickness, 0, 128);
        UiProperty Choice(params string[] values) => new(UiPropertyKind.Choice, Choices: values);
        void Add(string name, params (string Name, UiProperty Value)[] properties)
        {
            var component = components[name];
            components[name] = component with { Properties = component.Properties.SetItems(properties.Select(p => new KeyValuePair<string, UiProperty>(p.Name, p.Value))) };
        }
        // Derive structural contracts, never input/action authority, for new controls.
        var shape = components["Line"] with { Properties = components["Line"].Properties.Remove("StartPoint").Remove("EndPoint") };
        components.Add("Path", shape with { Name = "Path", Properties = shape.Properties.Add("Data", text) });
        components.Add("Polyline", shape with { Name = "Polyline", Properties = shape.Properties.Add("Points", text) });
        components.Add("Polygon", shape with { Name = "Polygon", Properties = shape.Properties.Add("Points", text).Add("FillRule", Choice("EvenOdd", "NonZero")) });
        components.Add("LayoutTransformControl", components["Viewbox"] with
        {
            Name = "LayoutTransformControl",
            Properties = components["Viewbox"].Properties.Remove("Stretch").Remove("StretchDirection").Add("LayoutTransform", text).Add("UseRenderTransform", boolean)
        });
        components.Add("Label", components["ContentControl"] with { Name = "Label" });
        foreach (var name in components.Keys.ToArray())
            Add(name, ("Margin", new(UiPropertyKind.Thickness, -10000, 10000)),
                ("ZIndex", new(UiPropertyKind.Integer, -32768, 32767)), ("IsHitTestVisible", boolean),
                ("UseLayoutRounding", boolean), ("FlowDirection", Choice("LeftToRight", "RightToLeft")),
                ("RenderTransform", text), ("RenderTransformOrigin", text), ("Clip", text));
        foreach (var name in new[] { "StackPanel", "Grid", "WrapPanel", "DockPanel", "UniformGrid" }) Add(name, ("Background", color));
        foreach (var name in TemplatedControls)
            Add(name, ("Background", color), ("BorderBrush", color), ("BorderThickness", thickness),
                ("Padding", thickness), ("CornerRadius", new(UiPropertyKind.CornerRadius, 0, 128)),
                ("FontFamily", text), ("FontSize", new(UiPropertyKind.Number, 0.1m, 512)),
                ("FontStyle", Choice("Normal", "Italic", "Oblique")),
                ("FontWeight", Choice("Normal", "Medium", "SemiBold", "Bold")), ("Foreground", color));
        foreach (var name in ContentControls)
            Add(name, ("HorizontalContentAlignment", Choice("Left", "Center", "Right", "Stretch")),
                ("VerticalContentAlignment", Choice("Top", "Center", "Bottom", "Stretch")));
        foreach (var name in new[] { "TextBlock", "SelectableTextBlock" })
            Add(name, ("FontFamily", text), ("FontSize", new(UiPropertyKind.Number, 0.1m, 512)),
                ("FontStyle", Choice("Normal", "Italic", "Oblique")),
                ("TextTrimming", Choice("None", "CharacterEllipsis", "WordEllipsis")),
                ("MaxLines", new(UiPropertyKind.Integer, 0, 4096)), ("LineHeight", new(UiPropertyKind.Number, 0.1m, 10000)));
        Add("TextBox", ("TextWrapping", Choice("NoWrap", "Wrap")), ("AcceptsTab", boolean));
        Add("ScrollViewer", ("HorizontalScrollBarVisibility", Choice("Disabled", "Auto", "Hidden", "Visible")),
            ("VerticalScrollBarVisibility", Choice("Disabled", "Auto", "Hidden", "Visible")),
            ("AllowAutoHide", boolean), ("Offset", new(UiPropertyKind.Point, 0, 1000000)));
        foreach (var name in new[] { "Rectangle", "Ellipse", "Line", "Path", "Polyline", "Polygon" })
            Add(name, ("Stretch", Choice("None", "Fill", "Uniform", "UniformToFill")),
                ("StrokeDashArray", text), ("StrokeDashOffset", new(UiPropertyKind.Number, -1000000, 1000000)),
                ("StrokeLineCap", Choice("Flat", "Round", "Square")), ("StrokeJoin", Choice("Miter", "Round", "Bevel")),
                ("StrokeMiterLimit", new(UiPropertyKind.Number, 1, 10000)));
        return components.Values;
    }

    internal static readonly string[] TemplatedControls =
    [
        "Button", "RepeatButton", "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch", "TextBox",
        "Slider", "ProgressBar", "Separator", "ScrollViewer", "ContentControl", "UserControl", "Expander",
        "TabControl", "TabItem", "ItemsControl", "ListBox", "ListBoxItem", "ComboBox", "ComboBoxItem",
        "TreeView", "TreeViewItem", "NumericUpDown", "DatePicker", "CalendarDatePicker", "Calendar", "TimePicker", "Label"
    ];
    internal static readonly string[] ContentControls =
    [
        "Button", "RepeatButton", "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch", "ScrollViewer",
        "ContentControl", "UserControl", "Expander", "TabItem", "ListBoxItem", "ComboBoxItem", "Label"
    ];
}
