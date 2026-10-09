using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace XamlG.IntelligentUI.Avalonia;

public sealed record UiControlRegistration(Func<Control> Create, ImmutableDictionary<string, Action<Control, JsonElement?>> Setters);

/// <summary>Only application-authored factories and typed property setters execute. No runtime XAML loader is used.</summary>
public sealed class UiAvaloniaCatalog
{
    public ImmutableDictionary<string, UiControlRegistration> Registrations { get; }
    public static UiAvaloniaCatalog Default { get; } = CreateDefault();
    public UiAvaloniaCatalog(IEnumerable<KeyValuePair<string, UiControlRegistration>> registrations) => Registrations = registrations.ToImmutableDictionary(StringComparer.Ordinal);
    private static UiAvaloniaCatalog CreateDefault()
    {
        var common = new Dictionary<string, Action<Control, JsonElement?>>(StringComparer.Ordinal);
        void Common<T>(string name, AvaloniaProperty<T> property, Func<JsonElement, T> convert) => common.Add(name, Set(property, convert));
        Common("Width", Control.WidthProperty, Number); Common("Height", Control.HeightProperty, Number);
        Common("MinWidth", Control.MinWidthProperty, Number); Common("MinHeight", Control.MinHeightProperty, Number);
        Common("MaxWidth", Control.MaxWidthProperty, Number); Common("MaxHeight", Control.MaxHeightProperty, Number);
        Common("Margin", Control.MarginProperty, value => new Thickness(Number(value)));
        Common("IsEnabled", Control.IsEnabledProperty, value => value.GetBoolean());
        Common("IsVisible", Control.IsVisibleProperty, value => value.GetBoolean());
        Common("HorizontalAlignment", Control.HorizontalAlignmentProperty, value => Enum.Parse<HorizontalAlignment>(value.GetString()!));
        Common("VerticalAlignment", Control.VerticalAlignmentProperty, value => Enum.Parse<VerticalAlignment>(value.GetString()!));
        Common("Grid.Row", Grid.RowProperty, Integer); Common("Grid.Column", Grid.ColumnProperty, Integer);
        Common("Grid.RowSpan", Grid.RowSpanProperty, Integer); Common("Grid.ColumnSpan", Grid.ColumnSpanProperty, Integer);
        var entries = new Dictionary<string, UiControlRegistration>(StringComparer.Ordinal);
        Dictionary<string, Action<Control, JsonElement?>> Current(string name, Func<Control> create)
        {
            var setters = new Dictionary<string, Action<Control, JsonElement?>>(common, StringComparer.Ordinal);
            entries.Add(name, new(create, ImmutableDictionary<string, Action<Control, JsonElement?>>.Empty));
            return setters;
        }
        void Finish(string name, Dictionary<string, Action<Control, JsonElement?>> setters) => entries[name] = entries[name] with { Setters = setters.ToImmutableDictionary(StringComparer.Ordinal) };
        var p = Current("StackPanel", () => new StackPanel());
        p["Spacing"] = Set(StackPanel.SpacingProperty, Number);
        p["Orientation"] = Set(StackPanel.OrientationProperty, value => Enum.Parse<Orientation>(value.GetString()!)); Finish("StackPanel", p);
        p = Current("Grid", () => new Grid());
        p["ColumnDefinitions"] = (control, value) => ((Grid)control).ColumnDefinitions = new ColumnDefinitions(value?.GetString() ?? "*");
        p["RowDefinitions"] = (control, value) => ((Grid)control).RowDefinitions = new RowDefinitions(value?.GetString() ?? "*"); Finish("Grid", p);
        p = Current("Border", () => new Border());
        p["Padding"] = Set(Border.PaddingProperty, value => new Thickness(Number(value)));
        p["CornerRadius"] = Set(Border.CornerRadiusProperty, value => new CornerRadius(Number(value)));
        p["BorderThickness"] = Set(Border.BorderThicknessProperty, value => new Thickness(Number(value)));
        p["BorderBrush"] = Set(Border.BorderBrushProperty, Brush); p["Background"] = Set(Border.BackgroundProperty, Brush); Finish("Border", p);
        p = Current("TextBlock", () => new TextBlock());
        p["Text"] = Set(TextBlock.TextProperty, value => value.GetString());
        p["FontSize"] = Set(TextBlock.FontSizeProperty, Number);
        p["FontWeight"] = Set(TextBlock.FontWeightProperty, value => value.GetString() switch { "Bold" => FontWeight.Bold, "SemiBold" => FontWeight.SemiBold, "Medium" => FontWeight.Medium, _ => FontWeight.Normal });
        p["Foreground"] = Set(TextBlock.ForegroundProperty, Brush);
        p["TextWrapping"] = Set(TextBlock.TextWrappingProperty, value => Enum.Parse<TextWrapping>(value.GetString()!)); Finish("TextBlock", p);
        p = Current("Button", () => new Button()); p["Content"] = Set(ContentControl.ContentProperty, value => (object?)value.GetString()); Finish("Button", p);
        p = Current("CheckBox", () => new CheckBox());
        p["Content"] = Set(ContentControl.ContentProperty, value => (object?)value.GetString());
        p["IsChecked"] = Set(ToggleButton.IsCheckedProperty, value => (bool?)value.GetBoolean()); Finish("CheckBox", p);
        p = Current("TextBox", () => new TextBox());
        p["Text"] = Set(TextBox.TextProperty, value => value.GetString());
        p["PlaceholderText"] = Set(TextBox.PlaceholderTextProperty, value => value.GetString());
        p["MaxLength"] = Set(TextBox.MaxLengthProperty, Integer);
        p["AcceptsReturn"] = Set(TextBox.AcceptsReturnProperty, value => value.GetBoolean()); Finish("TextBox", p);
        p = Current("Slider", () => new Slider());
        p["Value"] = Set(RangeBase.ValueProperty, Number); p["Minimum"] = Set(RangeBase.MinimumProperty, Number); p["Maximum"] = Set(RangeBase.MaximumProperty, Number);
        p["TickFrequency"] = Set(Slider.TickFrequencyProperty, Number);
        p["IsSnapToTickEnabled"] = Set(Slider.IsSnapToTickEnabledProperty, value => value.GetBoolean()); Finish("Slider", p);
        p = Current("ProgressBar", () => new ProgressBar());
        p["Value"] = Set(RangeBase.ValueProperty, Number); p["Minimum"] = Set(RangeBase.MinimumProperty, Number); p["Maximum"] = Set(RangeBase.MaximumProperty, Number);
        p["IsIndeterminate"] = Set(ProgressBar.IsIndeterminateProperty, value => value.GetBoolean()); Finish("ProgressBar", p);
        Finish("Separator", Current("Separator", () => new Separator()));
        Finish("ScrollViewer", Current("ScrollViewer", () => new ScrollViewer()));
        return new(entries);
    }
    private static Action<Control, JsonElement?> Set<T>(AvaloniaProperty<T> property, Func<JsonElement, T> convert) => (control, value) =>
    { if (value.HasValue) control.SetValue(property, convert(value.Value)); else control.ClearValue(property); };
    private static double Number(JsonElement value) => (double)value.GetDecimal();
    private static int Integer(JsonElement value)
    {
        var number = value.GetDecimal();
        return number == decimal.Truncate(number) ? checked((int)number) : throw new UiException("invalid_property", "Integer property required.");
    }
    private static IBrush? Brush(JsonElement value) => new SolidColorBrush(Color.Parse(value.GetString()!));
}
