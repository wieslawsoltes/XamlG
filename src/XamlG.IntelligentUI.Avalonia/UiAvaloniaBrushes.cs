using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Explicit typed constructors for validated inert brush values.</summary>
internal static class UiAvaloniaBrushes
{
    internal static void Set(Control control, string name, JsonElement? value)
    {
        AvaloniaProperty<IBrush?> property = name switch
        {
            "Background" when control is Border => Border.BackgroundProperty,
            "Background" when control is Panel => Panel.BackgroundProperty,
            "Background" => TemplatedControl.BackgroundProperty,
            "BorderBrush" when control is Border => Border.BorderBrushProperty,
            "BorderBrush" => TemplatedControl.BorderBrushProperty,
            "Foreground" => TextElement.ForegroundProperty,
            "Fill" => Shape.FillProperty,
            "Stroke" => Shape.StrokeProperty,
            _ => throw new UiException("invalid_brush", "Unregistered brush property.")
        };
        if (value is { } literal) control.SetValue(property, Read(literal)); else control.ClearValue(property);
    }
    internal static IBrush? Read(JsonElement value)
    {
        var description = UiBrushValues.Read(value);
        if (description == null) return null;
        Brush brush;
        if (description.Kind == "solid") brush = new SolidColorBrush(Color.Parse(description.Color!));
        else
        {
            GradientBrush gradient = description.Kind == "linear"
                ? new LinearGradientBrush { StartPoint = Point(description.StartPoint ?? "0%,0%"), EndPoint = Point(description.EndPoint ?? "100%,100%") }
                : new RadialGradientBrush { Center = Point(description.Center ?? "50%,50%"), GradientOrigin = Point(description.GradientOrigin ?? "50%,50%"), RadiusX = Radius(description.RadiusX ?? "50%"), RadiusY = Radius(description.RadiusY ?? "50%") };
            gradient.SpreadMethod = Enum.Parse<GradientSpreadMethod>(description.SpreadMethod ?? "Pad");
            foreach (var stop in description.Stops) gradient.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
            brush = gradient;
        }
        brush.Opacity = description.Opacity;
        if (description.Transform != null)
        {
            var m = UiDrawingValues.ReadMatrix(description.Transform); brush.Transform = new MatrixTransform(new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]));
        }
        if (description.TransformOrigin != null) brush.TransformOrigin = Point(description.TransformOrigin);
        return brush;
    }
    private static RelativePoint Point(string text)
    {
        var p = UiDrawingValues.ReadOrigin(text); return new RelativePoint(p.X, p.Y, p.Relative ? RelativeUnit.Relative : RelativeUnit.Absolute);
    }
    private static RelativeScalar Radius(string text)
    {
        var radius = UiBrushValues.ReadRadius(text); return new RelativeScalar(radius.Value, radius.Relative ? RelativeUnit.Relative : RelativeUnit.Absolute);
    }
}
