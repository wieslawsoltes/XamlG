using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Explicit native adapters for the additional schema. No reflection, asset loading,
/// runtime XAML parsing or arbitrary property lookup is used by the renderer.</summary>
internal static class UiAvaloniaFeatureCatalog
{
    internal static IEnumerable<KeyValuePair<string, UiControlRegistration>> Extend(Dictionary<string, UiControlRegistration> entries)
    {
        var shape = entries["Line"] with { Setters = entries["Line"].Setters.Remove("StartPoint").Remove("EndPoint") };
        entries.Add("Path", shape with { Create = () => new Path() });
        entries.Add("Polyline", shape with { Create = () => new Polyline() });
        entries.Add("Polygon", shape with { Create = () => new Polygon() });
        entries.Add("LayoutTransformControl", entries["Viewbox"] with
        {
            Create = () => new LayoutTransformControl(),
            Setters = entries["Viewbox"].Setters.Remove("Stretch").Remove("StretchDirection"),
            // Avalonia's opt-in render-transform bridge owns a property subscription.
            // Disabling the bridge releases it when the renderer retires the control.
            Retire = control => control.ClearValue(LayoutTransformControl.UseRenderTransformProperty)
        });
        entries.Add("Label", entries["ContentControl"] with { Create = () => new Label() });
        entries.Add("ContentPresenter", entries["ContentControl"] with { Create = () => new global::Avalonia.Controls.Presenters.ContentPresenter() });
        entries.Add("ItemsPresenter", entries["Viewbox"] with { Create = () => new global::Avalonia.Controls.Presenters.ItemsPresenter(),
            Setters = entries["Viewbox"].Setters.Remove("Stretch").Remove("StretchDirection") });
        UiAvaloniaInputCatalog.Extend(entries);
        var setters = new Dictionary<string, Action<Control, JsonElement?>>(StringComparer.Ordinal)
        {
            ["Classes"] = (control, value) =>
            {
                var classes = value is { } literal ? UiStyles.ReadClasses(literal.GetString()!) : [];
                foreach (var name in control.Classes.ToArray()) if (!name.StartsWith(':')) control.Classes.Remove(name);
                foreach (var name in classes) control.Classes.Add(name);
            },
            ["Name"] = Set(StyledElement.NameProperty, value => value.GetString()),
            ["ZIndex"] = Set(Control.ZIndexProperty, Integer),
            ["IsHitTestVisible"] = Set(Control.IsHitTestVisibleProperty, value => value.GetBoolean()),
            ["UseLayoutRounding"] = Set(Control.UseLayoutRoundingProperty, value => value.GetBoolean()),
            ["FlowDirection"] = Set(Control.FlowDirectionProperty, value => Enum.Parse<FlowDirection>(value.GetString()!)),
            ["RenderTransform"] = Set(Control.RenderTransformProperty, MatrixValue),
            ["RenderTransformOrigin"] = Set(Control.RenderTransformOriginProperty, Origin),
            ["Clip"] = Set(Control.ClipProperty, value => (Geometry?)Geometry.Parse(value.GetString()!)),
            ["LayoutTransform"] = Set(LayoutTransformControl.LayoutTransformProperty, MatrixValue),
            ["UseRenderTransform"] = Set(LayoutTransformControl.UseRenderTransformProperty, value => value.GetBoolean()),
            ["Data"] = Set(Path.DataProperty, value => (Geometry?)Geometry.Parse(value.GetString()!)),
            ["Points"] = (control, value) =>
            {
                if (control is Polygon) Set(Polygon.PointsProperty, Points)(control, value);
                else Set(Polyline.PointsProperty, Points)(control, value);
            },
            ["FillRule"] = Set(Polygon.FillRuleProperty, value => Enum.Parse<FillRule>(value.GetString()!)),
            ["Background"] = (control, value) =>
            {
                if (control is Panel) Set(Panel.BackgroundProperty, BrushValue)(control, value);
                else Set(TemplatedControl.BackgroundProperty, BrushValue)(control, value);
            },
            ["BorderBrush"] = Set(TemplatedControl.BorderBrushProperty, BrushValue),
            ["BorderThickness"] = Set(TemplatedControl.BorderThicknessProperty, ThicknessValue),
            ["Padding"] = Set(TemplatedControl.PaddingProperty, ThicknessValue),
            ["CornerRadius"] = Set(TemplatedControl.CornerRadiusProperty, CornerRadiusValue),
            ["FontFamily"] = Set(TextElement.FontFamilyProperty, value => new FontFamily(value.GetString()!)),
            ["FontSize"] = Set(TextElement.FontSizeProperty, Number),
            ["FontStyle"] = Set(TextElement.FontStyleProperty, value => Enum.Parse<FontStyle>(value.GetString()!)),
            ["FontWeight"] = Set(TextElement.FontWeightProperty, value => value.GetString() switch
            {
                "Bold" => FontWeight.Bold, "SemiBold" => FontWeight.SemiBold, "Medium" => FontWeight.Medium, _ => FontWeight.Normal
            }),
            ["Foreground"] = Set(TextElement.ForegroundProperty, BrushValue),
            ["HorizontalContentAlignment"] = Set(ContentControl.HorizontalContentAlignmentProperty, value => Enum.Parse<HorizontalAlignment>(value.GetString()!)),
            ["VerticalContentAlignment"] = Set(ContentControl.VerticalContentAlignmentProperty, value => Enum.Parse<VerticalAlignment>(value.GetString()!)),
            ["TextTrimming"] = Set(TextBlock.TextTrimmingProperty, value => TextTrimming.Parse(value.GetString()!)),
            ["MaxLines"] = Set(TextBlock.MaxLinesProperty, Integer),
            ["LineHeight"] = Set(TextBlock.LineHeightProperty, Number),
            ["TextWrapping"] = Set(TextBox.TextWrappingProperty, value => Enum.Parse<TextWrapping>(value.GetString()!)),
            ["AcceptsTab"] = Set(TextBox.AcceptsTabProperty, value => value.GetBoolean()),
            ["HorizontalScrollBarVisibility"] = Set(ScrollViewer.HorizontalScrollBarVisibilityProperty, value => Enum.Parse<ScrollBarVisibility>(value.GetString()!)),
            ["VerticalScrollBarVisibility"] = Set(ScrollViewer.VerticalScrollBarVisibilityProperty, value => Enum.Parse<ScrollBarVisibility>(value.GetString()!)),
            ["AllowAutoHide"] = Set(ScrollViewer.AllowAutoHideProperty, value => value.GetBoolean()),
            ["Offset"] = Set(ScrollViewer.OffsetProperty, value => { var points = Tuple(value); return new Vector(points[0], points[1]); }),
            ["Stretch"] = Set(Shape.StretchProperty, value => Enum.Parse<Stretch>(value.GetString()!)),
            ["StrokeDashArray"] = Set(Shape.StrokeDashArrayProperty, value => (AvaloniaList<double>?)new AvaloniaList<double>(UiDrawingValues.ReadDashes(value.GetString()!))),
            ["StrokeDashOffset"] = Set(Shape.StrokeDashOffsetProperty, Number),
            ["StrokeLineCap"] = Set(Shape.StrokeLineCapProperty, value => Enum.Parse<PenLineCap>(value.GetString()!)),
            ["StrokeJoin"] = Set(Shape.StrokeJoinProperty, value => Enum.Parse<PenLineJoin>(value.GetString()!)),
            ["StrokeMiterLimit"] = Set(Shape.StrokeMiterLimitProperty, Number)
        };
        foreach (var component in UiCatalog.Default.Components.Values)
        {
            var registration = entries[component.Name];
            var properties = registration.Setters.ToBuilder();
            foreach (var property in component.Properties.Keys)
                if (component.Properties[property].Kind == UiPropertyKind.Brush)
                    properties[property] = (control, value) => UiAvaloniaBrushes.Set(control, property, value);
                else if (!properties.ContainsKey(property)) properties.Add(property, setters[property]);
            entries[component.Name] = registration with { Setters = properties.ToImmutable() };
        }
        return entries;
    }

    private static Action<Control, JsonElement?> Set<T>(AvaloniaProperty<T> property, Func<JsonElement, T> convert) => (control, value) =>
    {
        if (value is { } literal) control.SetValue(property, convert(literal));
        else control.ClearValue(property);
    };
    private static double Number(JsonElement value) => (double)value.GetDecimal();
    private static int Integer(JsonElement value) => checked((int)value.GetDecimal());
    private static IBrush? BrushValue(JsonElement value) => Brush.Parse(value.GetString()!);
    private static ITransform? MatrixValue(JsonElement value)
    {
        var m = UiDrawingValues.ReadMatrix(value.GetString()!);
        return new MatrixTransform(new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]));
    }
    private static RelativePoint Origin(JsonElement value)
    {
        var origin = UiDrawingValues.ReadOrigin(value.GetString()!);
        return new RelativePoint(origin.X, origin.Y, origin.Relative ? RelativeUnit.Relative : RelativeUnit.Absolute);
    }
    private static IList<Point> Points(JsonElement value)
    {
        var coordinates = UiDrawingValues.ReadPoints(value.GetString()!);
        var points = new Point[coordinates.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new Point(coordinates[2 * i], coordinates[2 * i + 1]);
        return points;
    }
    // Thickness/point literals retain the existing catalog's comma/space grammar.
    // Geometry and matrices use the stricter drawing grammar instead.
    private static double[] Tuple(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? [Number(value)]
        : value.GetString()!.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => (double)decimal.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
    private static Thickness ThicknessValue(JsonElement value)
    {
        var p = Tuple(value);
        return p.Length switch { 1 => new(p[0]), 2 => new(p[0], p[1]), 4 => new(p[0], p[1], p[2], p[3]), _ => throw new UiException("invalid_property", "Invalid thickness.") };
    }
    private static CornerRadius CornerRadiusValue(JsonElement value)
    {
        var p = Tuple(value);
        return p.Length switch { 1 => new(p[0]), 2 => new(p[0], p[1], p[0], p[1]), 4 => new(p[0], p[1], p[2], p[3]), _ => throw new UiException("invalid_property", "Invalid corner radius.") };
    }
}
