using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XamlG.IntelligentUI;

public enum UiPropertyKind { Text, Number, Boolean, Color, Choice }
public sealed record UiProperty(UiPropertyKind Kind, decimal Minimum = 0, decimal Maximum = 10000, string[]? Choices = null)
{
    internal JsonElement ReadLiteral(string text) => Kind switch
    {
        UiPropertyKind.Number when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => Validate(UiJson.Element(number)),
        UiPropertyKind.Boolean when bool.TryParse(text, out var flag) => UiJson.Element(flag),
        UiPropertyKind.Text or UiPropertyKind.Color or UiPropertyKind.Choice => Validate(UiJson.Element(text)),
        _ => throw new UiException("invalid_property", "Invalid " + Kind + " literal.")
    };
    public JsonElement ValidateValue(JsonElement value) => Validate(value);
    internal JsonElement Validate(JsonElement value)
    {
        var valid = Kind switch
        {
            UiPropertyKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= Minimum && number <= Maximum,
            UiPropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            UiPropertyKind.Text => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16384,
            UiPropertyKind.Color => value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, "^(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|Transparent|Black|White|Gray|Red|Green|Blue)$", RegexOptions.CultureInvariant),
            UiPropertyKind.Choice => value.ValueKind == JsonValueKind.String && Choices?.Contains(value.GetString(), StringComparer.Ordinal) == true,
            _ => false
        };
        if (!valid) throw new UiException("invalid_property", "Property value violates its catalog type or bounds.");
        return value.Clone();
    }
}
public sealed record UiComponent(string Name, ImmutableDictionary<string, UiProperty> Properties, int MaximumChildren = 0,
    string? TextProperty = null, string? InputProperty = null);

/// <summary>An immutable allowlist shared by the compiler and renderer. Extenders must separately register a trusted native factory.</summary>
public sealed class UiCatalog
{
    public const string AvaloniaNamespace = "https://github.com/avaloniaui";
    public const string UiNamespace = "urn:xamlg:intelligent-ui";
    public ImmutableDictionary<string, UiComponent> Components { get; }
    public static UiCatalog Default { get; } = CreateDefault();
    public UiCatalog(IEnumerable<UiComponent> components)
    {
        Components = components.ToImmutableDictionary(c => c.Name, c => c with
        { Properties = c.Properties.ToImmutableDictionary(p => p.Key, p => p.Value with { Choices = p.Value.Choices?.ToArray() }, StringComparer.Ordinal) }, StringComparer.Ordinal);
    }
    internal UiComponent Get(string name) => Components.TryGetValue(name, out var component) ? component : throw new UiException("unknown_component", "Component is not in the catalog: " + name);
    private static UiCatalog CreateDefault()
    {
        var text = new UiProperty(UiPropertyKind.Text);
        var number = new UiProperty(UiPropertyKind.Number);
        var signed = new UiProperty(UiPropertyKind.Number, -1000000, 1000000);
        var boolean = new UiProperty(UiPropertyKind.Boolean);
        var color = new UiProperty(UiPropertyKind.Color);
        UiProperty Choice(params string[] values) => new(UiPropertyKind.Choice, Choices: values);
        var common = new Dictionary<string, UiProperty>(StringComparer.Ordinal)
        {
            ["Width"] = number, ["Height"] = number, ["MinWidth"] = number, ["MinHeight"] = number,
            ["MaxWidth"] = number, ["MaxHeight"] = number, ["Margin"] = new(UiPropertyKind.Number, 0, 128),
            ["IsVisible"] = boolean, ["IsEnabled"] = boolean,
            ["HorizontalAlignment"] = Choice("Left", "Center", "Right", "Stretch"),
            ["VerticalAlignment"] = Choice("Top", "Center", "Bottom", "Stretch"),
            ["Grid.Row"] = new(UiPropertyKind.Number, 0, 64), ["Grid.Column"] = new(UiPropertyKind.Number, 0, 64),
            ["Grid.RowSpan"] = new(UiPropertyKind.Number, 1, 64), ["Grid.ColumnSpan"] = new(UiPropertyKind.Number, 1, 64)
        };
        UiComponent C(string name, int children = 0, string? content = null, string? input = null, params (string Name, UiProperty Property)[] properties)
            => new(name, common.Concat(properties.Select(p => new KeyValuePair<string, UiProperty>(p.Name, p.Property))).ToImmutableDictionary(StringComparer.Ordinal), children, content, input);
        return new([
            C("StackPanel", 512, properties: [("Spacing", new(UiPropertyKind.Number, 0, 128)), ("Orientation", Choice("Horizontal", "Vertical"))]),
            C("Grid", 512, properties: [("ColumnDefinitions", text), ("RowDefinitions", text)]),
            C("Border", 1, properties: [("Padding", new(UiPropertyKind.Number, 0, 128)), ("CornerRadius", new(UiPropertyKind.Number, 0, 128)), ("BorderThickness", new(UiPropertyKind.Number, 0, 16)), ("BorderBrush", color), ("Background", color)]),
            C("TextBlock", content: "Text", properties: [("Text", text), ("FontSize", new(UiPropertyKind.Number, 8, 96)), ("FontWeight", Choice("Normal", "Medium", "SemiBold", "Bold")), ("Foreground", color), ("TextWrapping", Choice("NoWrap", "Wrap"))]),
            C("Button", content: "Content", properties: [("Content", text)]),
            C("TextBox", input: "Text", properties: [("Text", text), ("PlaceholderText", text), ("MaxLength", new(UiPropertyKind.Number, 1, 16384)), ("AcceptsReturn", boolean)]),
            C("Slider", input: "Value", properties: [("Value", signed), ("Minimum", signed), ("Maximum", signed), ("TickFrequency", new(UiPropertyKind.Number, 0.001m, 1000000)), ("IsSnapToTickEnabled", boolean)]),
            C("CheckBox", content: "Content", input: "IsChecked", properties: [("Content", text), ("IsChecked", boolean)]),
            C("ProgressBar", properties: [("Value", signed), ("Minimum", signed), ("Maximum", signed), ("IsIndeterminate", boolean)]),
            C("Separator"),
            C("ScrollViewer", 1)
        ]);
    }
}
