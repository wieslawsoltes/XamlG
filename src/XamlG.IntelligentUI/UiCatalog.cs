using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XamlG.IntelligentUI;

public enum UiPropertyKind { Text, Number, Boolean, Color, Choice, Integer, Thickness, CornerRadius, Point, Date, Time, StringArray }
public sealed record UiProperty(UiPropertyKind Kind, decimal Minimum = 0, decimal Maximum = 10000, string[]? Choices = null, bool Nullable = false)
{
    internal JsonElement ReadLiteral(string text)
    {
        if (Nullable && text.Length == 0) return UiJson.Element(null);
        return Kind switch
        {
            UiPropertyKind.Number or UiPropertyKind.Integer when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => Validate(UiJson.Element(number)),
            UiPropertyKind.Boolean when bool.TryParse(text, out var flag) => UiJson.Element(flag),
            UiPropertyKind.Thickness or UiPropertyKind.CornerRadius when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar) => Validate(UiJson.Element(scalar)),
            UiPropertyKind.StringArray => ReadArray(text),
            UiPropertyKind.Text or UiPropertyKind.Color or UiPropertyKind.Choice or UiPropertyKind.Thickness or UiPropertyKind.CornerRadius or UiPropertyKind.Point or UiPropertyKind.Date or UiPropertyKind.Time => Validate(UiJson.Element(text)),
            _ => throw new UiException("invalid_property", "Invalid " + Kind + " literal.")
        };
    }
    private JsonElement ReadArray(string text)
    {
        try { using var document = JsonDocument.Parse(text); return Validate(document.RootElement); }
        catch (JsonException) { throw new UiException("invalid_property", "Use a JSON array of strings."); }
    }
    public JsonElement ValidateValue(JsonElement value) => Validate(value);
    internal JsonElement Validate(JsonElement value)
    {
        if (Nullable && value.ValueKind == JsonValueKind.Null) return value.Clone();
        var valid = Kind switch
        {
            UiPropertyKind.Number => Numeric(value, false), UiPropertyKind.Integer => Numeric(value, true),
            UiPropertyKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            UiPropertyKind.Text => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 16384,
            UiPropertyKind.Color => value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, "^(#[0-9a-fA-F]{3}|#[0-9a-fA-F]{4}|#[0-9a-fA-F]{6}|#[0-9a-fA-F]{8}|Transparent|Black|White|Gray|Red|Green|Blue|Orange|Yellow|Purple|Pink|Silver|Navy|Teal|Lime|Maroon|Olive|Aqua|Fuchsia)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
            UiPropertyKind.Choice => value.ValueKind == JsonValueKind.String && Choices?.Contains(value.GetString(), StringComparer.Ordinal) == true,
            UiPropertyKind.Thickness or UiPropertyKind.CornerRadius => Tuple(value, 1, 2, 4),
            UiPropertyKind.Point => Tuple(value, 2),
            UiPropertyKind.Date => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 40 && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            UiPropertyKind.Time => value.ValueKind == JsonValueKind.String && TimeSpan.TryParseExact(value.GetString(), ["c", "hh\\:mm", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1),
            UiPropertyKind.StringArray => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 512 && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString()!.Length <= 1024),
            _ => false
        };
        if (!valid) throw new UiException("invalid_property", "Property value violates its catalog type or bounds: " + Kind);
        return value.Clone();
    }
    private bool Numeric(JsonElement value, bool integer) => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= Minimum && number <= Maximum && (!integer || number == decimal.Truncate(number));
    private bool Tuple(JsonElement value, params int[] counts)
    {
        if (value.ValueKind == JsonValueKind.Number) return counts.Contains(1) && Numeric(value, false);
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 256) return false;
        var parts = value.GetString()!.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return counts.Contains(parts.Length) && parts.All(part => decimal.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= Minimum && number <= Maximum);
    }
}
public sealed record UiComponent(string Name, ImmutableDictionary<string, UiProperty> Properties, int MaximumChildren = 0,
    string? TextProperty = null, string? InputProperty = null, bool SupportsAction = false, string[]? ChildTypes = null);

/// <summary>Immutable component declarations. Every native factory and property setter remains registered by the embedding application.</summary>
public sealed class UiCatalog
{
    public const string AvaloniaNamespace = "https://github.com/avaloniaui";
    public const string UiNamespace = "urn:xamlg:intelligent-ui";
    public ImmutableDictionary<string, UiComponent> Components { get; }
    public static UiCatalog Default { get; } = CreateDefault();
    public UiCatalog(IEnumerable<UiComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        Components = components.ToImmutableDictionary(c => c.Name, c => c with
        { Properties = c.Properties.ToImmutableDictionary(p => p.Key, p => p.Value with { Choices = p.Value.Choices?.ToArray() }, StringComparer.Ordinal), ChildTypes = c.ChildTypes?.ToArray() }, StringComparer.Ordinal);
        foreach (var component in Components.Values)
        {
            UiJson.Identifier(component.Name, "Component name");
            if (component.MaximumChildren is < 0 or > 4096 || component.InputProperty != null && !component.Properties.ContainsKey(component.InputProperty) || component.TextProperty != null && !component.Properties.ContainsKey(component.TextProperty))
                throw new ArgumentException("Invalid component declaration: " + component.Name);
        }
    }
    internal UiComponent Get(string name) => Components.TryGetValue(name, out var component) ? component : throw new UiException("unknown_component", "Component is not in the catalog: " + name);
    private static UiCatalog CreateDefault()
    {
        var text = new UiProperty(UiPropertyKind.Text);
        var number = new UiProperty(UiPropertyKind.Number);
        var signed = new UiProperty(UiPropertyKind.Number, -1000000, 1000000);
        var boolean = new UiProperty(UiPropertyKind.Boolean);
        var color = new UiProperty(UiPropertyKind.Color);
        var thickness = new UiProperty(UiPropertyKind.Thickness, 0, 128);
        var index = new UiProperty(UiPropertyKind.Integer, -1, 4095);
        UiProperty Choice(params string[] values) => new(UiPropertyKind.Choice, Choices: values);
        var common = new Dictionary<string, UiProperty>(StringComparer.Ordinal)
        {
            ["Width"] = number, ["Height"] = number, ["MinWidth"] = number, ["MinHeight"] = number,
            ["MaxWidth"] = number, ["MaxHeight"] = number, ["Margin"] = thickness,
            ["IsVisible"] = boolean, ["IsEnabled"] = boolean, ["Focusable"] = boolean, ["ClipToBounds"] = boolean,
            ["Opacity"] = new(UiPropertyKind.Number, 0, 1), ["ToolTip.Tip"] = text, ["AutomationProperties.Name"] = text,
            ["HorizontalAlignment"] = Choice("Left", "Center", "Right", "Stretch"),
            ["VerticalAlignment"] = Choice("Top", "Center", "Bottom", "Stretch"),
            ["Grid.Row"] = new(UiPropertyKind.Integer, 0, 63), ["Grid.Column"] = new(UiPropertyKind.Integer, 0, 63),
            ["Grid.RowSpan"] = new(UiPropertyKind.Integer, 1, 64), ["Grid.ColumnSpan"] = new(UiPropertyKind.Integer, 1, 64),
            ["DockPanel.Dock"] = Choice("Left", "Top", "Right", "Bottom"),
            ["Canvas.Left"] = signed, ["Canvas.Top"] = signed, ["Canvas.Right"] = signed, ["Canvas.Bottom"] = signed
        };
        var components = new List<UiComponent>();
        void C(string name, int children = 0, string? content = null, string? input = null, bool action = false, string[]? childTypes = null, params (string Name, UiProperty Property)[] properties)
            => components.Add(new(name, common.Concat(properties.Select(p => new KeyValuePair<string, UiProperty>(p.Name, p.Property))).ToImmutableDictionary(StringComparer.Ordinal), children, content, input, action, childTypes));
        C("StackPanel", 512, properties: [("Spacing", new(UiPropertyKind.Number, 0, 128)), ("Orientation", Choice("Horizontal", "Vertical"))]);
        C("Grid", 512, properties: [("ColumnDefinitions", text), ("RowDefinitions", text)]);
        C("Border", 1, properties: [("Padding", thickness), ("CornerRadius", new(UiPropertyKind.CornerRadius, 0, 128)), ("BorderThickness", new(UiPropertyKind.Thickness, 0, 16)), ("BorderBrush", color), ("Background", color)]);
        foreach (var name in new[] { "TextBlock", "SelectableTextBlock" })
            C(name, content: "Text", properties: [("Text", text), ("FontSize", new(UiPropertyKind.Number, 8, 96)), ("FontWeight", Choice("Normal", "Medium", "SemiBold", "Bold")), ("Foreground", color), ("TextWrapping", Choice("NoWrap", "Wrap")), ("TextAlignment", Choice("Left", "Center", "Right", "Justify"))]);
        foreach (var name in new[] { "Button", "RepeatButton" }) C(name, 1, "Content", action: true, properties: [("Content", text)]);
        C("TextBox", input: "Text", properties: [("Text", text), ("PlaceholderText", text), ("MaxLength", new(UiPropertyKind.Integer, 1, 16384)), ("AcceptsReturn", boolean), ("IsReadOnly", boolean), ("TextAlignment", Choice("Left", "Center", "Right", "Justify"))]);
        C("Slider", input: "Value", properties: [("Value", signed), ("Minimum", signed), ("Maximum", signed), ("TickFrequency", new(UiPropertyKind.Number, 0.001m, 1000000)), ("IsSnapToTickEnabled", boolean), ("Orientation", Choice("Horizontal", "Vertical"))]);
        foreach (var name in new[] { "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch" })
            C(name, 1, "Content", "IsChecked", properties: [("Content", text), ("IsChecked", boolean)]);
        C("ProgressBar", properties: [("Value", signed), ("Minimum", signed), ("Maximum", signed), ("IsIndeterminate", boolean)]);
        C("Separator"); C("ScrollViewer", 1);
        C("Panel", 512, properties: [("Background", color)]); C("Canvas", 512, properties: [("Background", color)]);
        C("WrapPanel", 512, properties: [("Orientation", Choice("Horizontal", "Vertical")), ("ItemWidth", number), ("ItemHeight", number)]);
        C("DockPanel", 512, properties: [("LastChildFill", boolean)]);
        C("UniformGrid", 512, properties: [("Rows", new(UiPropertyKind.Integer, 0, 64)), ("Columns", new(UiPropertyKind.Integer, 0, 64)), ("FirstColumn", new(UiPropertyKind.Integer, 0, 63))]);
        C("Viewbox", 1, properties: [("Stretch", Choice("None", "Fill", "Uniform", "UniformToFill")), ("StretchDirection", Choice("UpOnly", "DownOnly", "Both"))]);
        foreach (var name in new[] { "ContentControl", "UserControl" }) C(name, 1, "Content", properties: [("Content", text)]);
        C("Expander", 1, "Content", "IsExpanded", properties: [("Header", text), ("Content", text), ("IsExpanded", boolean), ("ExpandDirection", Choice("Down", "Up", "Left", "Right"))]);
        C("TabControl", 128, input: "SelectedIndex", childTypes: ["TabItem"], properties: [("SelectedIndex", index), ("TabStripPlacement", Choice("Left", "Top", "Right", "Bottom"))]);
        C("TabItem", 1, "Content", properties: [("Header", text), ("Content", text)]);
        C("ItemsControl", 512, properties: [("ItemsSource", new(UiPropertyKind.StringArray))]);
        C("ListBox", 512, input: "SelectedIndex", properties: [("SelectedIndex", index), ("ItemsSource", new(UiPropertyKind.StringArray))]);
        C("ListBoxItem", 1, "Content", properties: [("Content", text)]);
        C("ComboBox", 512, input: "SelectedIndex", properties: [("SelectedIndex", index), ("ItemsSource", new(UiPropertyKind.StringArray)), ("PlaceholderText", text)]);
        C("ComboBoxItem", 1, "Content", properties: [("Content", text)]);
        C("TreeView", 512, childTypes: ["TreeViewItem"]);
        C("TreeViewItem", 512, "Header", "IsExpanded", childTypes: ["TreeViewItem"], properties: [("Header", text), ("IsExpanded", boolean)]);
        C("NumericUpDown", input: "Value", properties: [("Value", signed with { Nullable = true }), ("Minimum", signed), ("Maximum", signed), ("Increment", new(UiPropertyKind.Number, 0.001m, 1000000)), ("FormatString", text)]);
        C("DatePicker", input: "SelectedDate", properties: [("SelectedDate", new(UiPropertyKind.Date, Nullable: true)), ("DayVisible", boolean), ("MonthVisible", boolean), ("YearVisible", boolean)]);
        C("CalendarDatePicker", input: "SelectedDate", properties: [("SelectedDate", new(UiPropertyKind.Date, Nullable: true)), ("PlaceholderText", text)]);
        C("Calendar", input: "SelectedDate", properties: [("SelectedDate", new(UiPropertyKind.Date, Nullable: true))]);
        C("TimePicker", input: "SelectedTime", properties: [("SelectedTime", new(UiPropertyKind.Time, Nullable: true)), ("MinuteIncrement", new(UiPropertyKind.Integer, 1, 59)), ("ClockIdentifier", Choice("12HourClock", "24HourClock"))]);
        foreach (var name in new[] { "Rectangle", "Ellipse", "Line" })
        {
            var properties = new List<(string, UiProperty)> { ("Fill", color), ("Stroke", color), ("StrokeThickness", new(UiPropertyKind.Number, 0, 128)) };
            if (name == "Rectangle") properties.AddRange([("RadiusX", number), ("RadiusY", number)]);
            if (name == "Line") properties.AddRange([("StartPoint", new(UiPropertyKind.Point, -1000000, 1000000)), ("EndPoint", new(UiPropertyKind.Point, -1000000, 1000000))]);
            C(name, properties: properties.ToArray());
        }
        return new(UiAvaloniaFeatureSchema.Extend(components));
    }
}
