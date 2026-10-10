using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

public sealed record UiGradientStop(string Color, double Offset);
public sealed record UiBrushDefinition(string Kind, string? Color, double Opacity, string? Transform,
    string? TransformOrigin, string? SpreadMethod, string? StartPoint, string? EndPoint,
    string? Center, string? GradientOrigin, string? RadiusX, string? RadiusY, ImmutableArray<UiGradientStop> Stops);

/// <summary>Bounded, data-only brush transport shared by authoring, native adapters and export.
/// No image URI, type name, resource fetch or executable brush factory can occur in this format.</summary>
public static class UiBrushValues
{
    public const int MaximumStops = 64;
    public const int MaximumCharacters = 16384;
    private static readonly UiProperty ColorType = new(UiPropertyKind.Color);
    public static bool IsProperty(string name) => name is "Background" or "Foreground" or "BorderBrush" or "Fill" or "Stroke";
    public static JsonElement ParseLiteral(string text)
    {
        if (text.Length > MaximumCharacters) throw Invalid("Brush text exceeds its budget.");
        try
        {
            var value = text.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(text) : UiJson.Element(text);
            Read(value); return value;
        }
        catch (JsonException) { throw Invalid("Malformed brush declaration."); }
    }
    public static UiBrushDefinition? Read(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
            return new("solid", Color(value), 1, null, null, null, null, null, null, null, null, null, []);
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText().Length > MaximumCharacters) throw Invalid("Expected a bounded brush object or color.");
        var fields = Fields(value, ["kind", "color", "opacity", "transform", "transformOrigin", "spreadMethod", "startPoint", "endPoint", "center", "gradientOrigin", "radiusX", "radiusY", "stops"]);
        var kind = Text(fields, "kind", required: true);
        if (kind is not ("solid" or "linear" or "radial")) throw Invalid("Unknown brush kind.");
        var allowed = kind switch
        {
            "solid" => new[] { "kind", "color", "opacity", "transform", "transformOrigin" },
            "linear" => ["kind", "opacity", "transform", "transformOrigin", "spreadMethod", "startPoint", "endPoint", "stops"],
            _ => ["kind", "opacity", "transform", "transformOrigin", "spreadMethod", "center", "gradientOrigin", "radiusX", "radiusY", "stops"]
        };
        if (fields.Keys.Any(name => !allowed.Contains(name, StringComparer.Ordinal))) throw Invalid("Property does not belong to this brush kind.");
        var opacity = fields.TryGetValue("opacity", out var alpha) ? Number(alpha, 0, 1) : 1;
        var transform = Text(fields, "transform"); if (transform != null) UiDrawingValues.ReadMatrix(transform);
        var origin = Text(fields, "transformOrigin"); if (origin != null) UiDrawingValues.ReadOrigin(origin);
        var spread = Text(fields, "spreadMethod");
        if (spread is not (null or "Pad" or "Reflect" or "Repeat")) throw Invalid("Invalid gradient spread method.");
        string? Point(string name)
        {
            var text = Text(fields, name); if (text != null) UiDrawingValues.ReadOrigin(text); return text;
        }
        string? Radius(string name)
        {
            var text = Text(fields, name); if (text != null) ReadRadius(text); return text;
        }
        var stops = ImmutableArray.CreateBuilder<UiGradientStop>();
        if (kind != "solid")
        {
            if (!fields.TryGetValue("stops", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > MaximumStops)
                throw Invalid("Gradients require a bounded stop array.");
            var previous = -1d;
            foreach (var stop in values.EnumerateArray())
            {
                var parts = Fields(stop, ["color", "offset"]);
                if (!parts.TryGetValue("color", out var stopColor) || !parts.TryGetValue("offset", out var offset)) throw Invalid("Gradient stops require color and offset.");
                var position = Number(offset, 0, 1);
                if (position < previous) throw Invalid("Gradient stops must be ordered by offset.");
                previous = position; stops.Add(new(Color(stopColor), position));
            }
        }
        var solid = kind == "solid" ? fields.TryGetValue("color", out var solidColor) ? Color(solidColor) : "Transparent" : null;
        return new(kind, solid, opacity, transform, origin, spread, Point("startPoint"), Point("endPoint"),
            Point("center"), Point("gradientOrigin"), Radius("radiusX"), Radius("radiusY"), stops.ToImmutable());
    }
    public static (double Value, bool Relative) ReadRadius(string source)
    {
        if (source.Length is 0 or > 80) throw Invalid("Invalid gradient radius.");
        var text = source.AsSpan().Trim(); var relative = text.EndsWith("%", StringComparison.Ordinal);
        if (relative) text = text[..^1];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0 || value > 1000000)
            throw Invalid("Gradient radius must be positive and finite.");
        return (relative ? value / 100 : value, relative);
    }
    public static XElement ToXaml(JsonElement value)
    {
        XNamespace ns = UiCatalog.AvaloniaNamespace;
        var brush = Read(value);
        if (brush == null) return new XElement(XName.Get("Null", "http://schemas.microsoft.com/winfx/2006/xaml"));
        var tag = brush.Kind switch { "solid" => "SolidColorBrush", "linear" => "LinearGradientBrush", _ => "RadialGradientBrush" };
        var node = new XElement(ns + tag);
        void Set(string name, object? item) { if (item != null) node.Add(new XAttribute(name, item)); }
        Set("Color", brush.Color); Set("Opacity", brush.Opacity.ToString("R", CultureInfo.InvariantCulture));
        Set("Transform", brush.Transform); Set("TransformOrigin", brush.TransformOrigin); Set("SpreadMethod", brush.SpreadMethod);
        Set("StartPoint", brush.StartPoint); Set("EndPoint", brush.EndPoint); Set("Center", brush.Center);
        Set("GradientOrigin", brush.GradientOrigin); Set("RadiusX", brush.RadiusX); Set("RadiusY", brush.RadiusY);
        foreach (var stop in brush.Stops) node.Add(new XElement(ns + "GradientStop", new XAttribute("Color", stop.Color), new XAttribute("Offset", stop.Offset.ToString("R", CultureInfo.InvariantCulture))));
        return node;
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Expected a brush object.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value)) throw Invalid("Unknown or duplicate brush field.");
        return fields;
    }
    private static string? Text(Dictionary<string, JsonElement> fields, string name, bool required = false)
    {
        if (!fields.TryGetValue(name, out var value)) return required ? throw Invalid("Missing brush field: " + name) : null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 256) throw Invalid("Expected bounded brush text.");
        return value.GetString();
    }
    private static double Number(JsonElement value, double min, double max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max)
            throw Invalid("Brush number exceeds its bounds.");
        return number;
    }
    private static string Color(JsonElement value) { ColorType.ValidateValue(value); return value.GetString()!; }
    private static UiException Invalid(string message) => new("invalid_brush", message);
}
