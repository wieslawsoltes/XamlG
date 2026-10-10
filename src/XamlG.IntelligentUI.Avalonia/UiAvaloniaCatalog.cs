using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Calendar = Avalonia.Controls.Calendar;

namespace XamlG.IntelligentUI.Avalonia;

public sealed record UiControlRegistration(Func<Control> Create, ImmutableDictionary<string, Action<Control, JsonElement?>> Setters,
    Func<Control, AvaloniaPropertyChangedEventArgs, JsonElement?>? ReadInput = null, Action<Control>? Retire = null);

/// <summary>Application-authored constructors, typed setters and input adapters. No reflective control creation is required by the browser backend.</summary>
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
        Common("Margin", Control.MarginProperty, ThicknessValue);
        Common("IsEnabled", Control.IsEnabledProperty, value => value.GetBoolean()); Common("IsVisible", Control.IsVisibleProperty, value => value.GetBoolean());
        Common("Focusable", Control.FocusableProperty, value => value.GetBoolean()); Common("ClipToBounds", Control.ClipToBoundsProperty, value => value.GetBoolean());
        Common("Opacity", Control.OpacityProperty, Number);
        Common("ToolTip.Tip", ToolTip.TipProperty, value => (object?)value.GetString());
        Common("AutomationProperties.Name", AutomationProperties.NameProperty, value => value.GetString());
        Common("HorizontalAlignment", Control.HorizontalAlignmentProperty, value => Enum.Parse<HorizontalAlignment>(value.GetString()!));
        Common("VerticalAlignment", Control.VerticalAlignmentProperty, value => Enum.Parse<VerticalAlignment>(value.GetString()!));
        Common("Grid.Row", Grid.RowProperty, Integer); Common("Grid.Column", Grid.ColumnProperty, Integer);
        Common("Grid.RowSpan", Grid.RowSpanProperty, Integer); Common("Grid.ColumnSpan", Grid.ColumnSpanProperty, Integer);
        Common("DockPanel.Dock", DockPanel.DockProperty, value => Enum.Parse<Dock>(value.GetString()!));
        Common("Canvas.Left", Canvas.LeftProperty, Number); Common("Canvas.Top", Canvas.TopProperty, Number);
        Common("Canvas.Right", Canvas.RightProperty, Number); Common("Canvas.Bottom", Canvas.BottomProperty, Number);
        var entries = new Dictionary<string, UiControlRegistration>(StringComparer.Ordinal);
        void Add<T>(string name, Action<Dictionary<string, Action<Control, JsonElement?>>>? configure = null,
            Func<Control, AvaloniaPropertyChangedEventArgs, JsonElement?>? input = null) where T : Control, new()
        {
            var setters = new Dictionary<string, Action<Control, JsonElement?>>(common, StringComparer.Ordinal); configure?.Invoke(setters);
            entries.Add(name, new(() => new T(), setters.ToImmutableDictionary(StringComparer.Ordinal), input));
        }
        void Content(Dictionary<string, Action<Control, JsonElement?>> p) => p["Content"] = Set(ContentControl.ContentProperty, value => (object?)value.GetString());
        void Text(Dictionary<string, Action<Control, JsonElement?>> p)
        {
            p["Text"] = Set(TextBlock.TextProperty, value => value.GetString()); p["FontSize"] = Set(TextBlock.FontSizeProperty, Number);
            p["FontWeight"] = Set(TextBlock.FontWeightProperty, value => value.GetString() switch { "Bold" => FontWeight.Bold, "SemiBold" => FontWeight.SemiBold, "Medium" => FontWeight.Medium, _ => FontWeight.Normal });
            p["Foreground"] = Set(TextBlock.ForegroundProperty, Brush); p["TextWrapping"] = Set(TextBlock.TextWrappingProperty, value => Enum.Parse<TextWrapping>(value.GetString()!));
            p["TextAlignment"] = Set(TextBlock.TextAlignmentProperty, value => Enum.Parse<TextAlignment>(value.GetString()!));
        }
        void Toggle(Dictionary<string, Action<Control, JsonElement?>> p) { Content(p); p["IsChecked"] = Set(ToggleButton.IsCheckedProperty, value => (bool?)value.GetBoolean()); }
        void Items(Dictionary<string, Action<Control, JsonElement?>> p) => p["ItemsSource"] = Set(ItemsControl.ItemsSourceProperty, value => (System.Collections.IEnumerable?)value.EnumerateArray().Select(item => item.GetString()!).ToArray());
        void Select(Dictionary<string, Action<Control, JsonElement?>> p) => p["SelectedIndex"] = Set(SelectingItemsControl.SelectedIndexProperty, Integer);
        void Shape(Dictionary<string, Action<Control, JsonElement?>> p)
        {
            p["Fill"] = Set(global::Avalonia.Controls.Shapes.Shape.FillProperty, Brush); p["Stroke"] = Set(global::Avalonia.Controls.Shapes.Shape.StrokeProperty, Brush);
            p["StrokeThickness"] = Set(global::Avalonia.Controls.Shapes.Shape.StrokeThicknessProperty, Number);
        }
        Add<StackPanel>("StackPanel", p => { p["Spacing"] = Set(StackPanel.SpacingProperty, Number); p["Orientation"] = Set(StackPanel.OrientationProperty, value => Enum.Parse<Orientation>(value.GetString()!)); });
        Add<Grid>("Grid", p =>
        {
            p["ColumnDefinitions"] = (control, value) => ((Grid)control).ColumnDefinitions = new ColumnDefinitions(value?.GetString() ?? "*");
            p["RowDefinitions"] = (control, value) => ((Grid)control).RowDefinitions = new RowDefinitions(value?.GetString() ?? "*");
        });
        Add<Border>("Border", p =>
        {
            p["Padding"] = Set(Border.PaddingProperty, ThicknessValue); p["CornerRadius"] = Set(Border.CornerRadiusProperty, CornerRadiusValue);
            p["BorderThickness"] = Set(Border.BorderThicknessProperty, ThicknessValue); p["BorderBrush"] = Set(Border.BorderBrushProperty, Brush); p["Background"] = Set(Border.BackgroundProperty, Brush);
        });
        Add<TextBlock>("TextBlock", Text); Add<SelectableTextBlock>("SelectableTextBlock", Text);
        Add<Button>("Button", Content); Add<RepeatButton>("RepeatButton", Content);
        Add<CheckBox>("CheckBox", Toggle, Input(ToggleButton.IsCheckedProperty, value => value == true));
        Add<ToggleButton>("ToggleButton", Toggle, Input(ToggleButton.IsCheckedProperty, value => value == true));
        Add<RadioButton>("RadioButton", Toggle, Input(ToggleButton.IsCheckedProperty, value => value == true));
        Add<ToggleSwitch>("ToggleSwitch", Toggle, Input(ToggleButton.IsCheckedProperty, value => value == true));
        Add<TextBox>("TextBox", p =>
        {
            p["Text"] = Set(TextBox.TextProperty, value => value.GetString()); p["PlaceholderText"] = Set(TextBox.PlaceholderTextProperty, value => value.GetString());
            p["MaxLength"] = Set(TextBox.MaxLengthProperty, Integer); p["AcceptsReturn"] = Set(TextBox.AcceptsReturnProperty, value => value.GetBoolean());
            p["IsReadOnly"] = Set(TextBox.IsReadOnlyProperty, value => value.GetBoolean()); p["TextAlignment"] = Set(TextBox.TextAlignmentProperty, value => Enum.Parse<TextAlignment>(value.GetString()!));
        }, Input(TextBox.TextProperty, value => value ?? ""));
        Add<Slider>("Slider", p =>
        {
            p["Value"] = Set(RangeBase.ValueProperty, Number); p["Minimum"] = Set(RangeBase.MinimumProperty, Number); p["Maximum"] = Set(RangeBase.MaximumProperty, Number);
            p["TickFrequency"] = Set(Slider.TickFrequencyProperty, Number); p["IsSnapToTickEnabled"] = Set(Slider.IsSnapToTickEnabledProperty, value => value.GetBoolean());
            p["Orientation"] = Set(Slider.OrientationProperty, value => Enum.Parse<Orientation>(value.GetString()!));
        }, Input(RangeBase.ValueProperty, value => value));
        Add<ProgressBar>("ProgressBar", p =>
        {
            p["Value"] = Set(RangeBase.ValueProperty, Number); p["Minimum"] = Set(RangeBase.MinimumProperty, Number); p["Maximum"] = Set(RangeBase.MaximumProperty, Number);
            p["IsIndeterminate"] = Set(ProgressBar.IsIndeterminateProperty, value => value.GetBoolean());
        });
        Add<Separator>("Separator"); Add<ScrollViewer>("ScrollViewer");
        Add<Panel>("Panel", p => p["Background"] = Set(Panel.BackgroundProperty, Brush)); Add<Canvas>("Canvas", p => p["Background"] = Set(Panel.BackgroundProperty, Brush));
        Add<WrapPanel>("WrapPanel", p => { p["Orientation"] = Set(WrapPanel.OrientationProperty, value => Enum.Parse<Orientation>(value.GetString()!)); p["ItemWidth"] = Set(WrapPanel.ItemWidthProperty, Number); p["ItemHeight"] = Set(WrapPanel.ItemHeightProperty, Number); });
        Add<DockPanel>("DockPanel", p => p["LastChildFill"] = Set(DockPanel.LastChildFillProperty, value => value.GetBoolean()));
        Add<UniformGrid>("UniformGrid", p => { p["Rows"] = Set(UniformGrid.RowsProperty, Integer); p["Columns"] = Set(UniformGrid.ColumnsProperty, Integer); p["FirstColumn"] = Set(UniformGrid.FirstColumnProperty, Integer); });
        Add<Viewbox>("Viewbox", p => { p["Stretch"] = Set(Viewbox.StretchProperty, value => Enum.Parse<Stretch>(value.GetString()!)); p["StretchDirection"] = Set(Viewbox.StretchDirectionProperty, value => Enum.Parse<StretchDirection>(value.GetString()!)); });
        Add<ContentControl>("ContentControl", Content); Add<UserControl>("UserControl", Content);
        Add<Expander>("Expander", p =>
        {
            Content(p); p["Header"] = Set(HeaderedContentControl.HeaderProperty, value => (object?)value.GetString()); p["IsExpanded"] = Set(Expander.IsExpandedProperty, value => value.GetBoolean());
            p["ExpandDirection"] = Set(Expander.ExpandDirectionProperty, value => Enum.Parse<ExpandDirection>(value.GetString()!));
        }, Input(Expander.IsExpandedProperty, value => value));
        Add<TabControl>("TabControl", p => { Select(p); p["TabStripPlacement"] = Set(TabControl.TabStripPlacementProperty, value => Enum.Parse<Dock>(value.GetString()!)); }, Input(SelectingItemsControl.SelectedIndexProperty, value => value));
        Add<TabItem>("TabItem", p => { Content(p); p["Header"] = Set(HeaderedContentControl.HeaderProperty, value => (object?)value.GetString()); });
        Add<ItemsControl>("ItemsControl", Items);
        Add<ListBox>("ListBox", p => { Items(p); Select(p); }, Input(SelectingItemsControl.SelectedIndexProperty, value => value));
        Add<ListBoxItem>("ListBoxItem", Content);
        Add<ComboBox>("ComboBox", p => { Items(p); Select(p); p["PlaceholderText"] = Set(ComboBox.PlaceholderTextProperty, value => value.GetString()); }, Input(SelectingItemsControl.SelectedIndexProperty, value => value));
        Add<ComboBoxItem>("ComboBoxItem", Content);
        Add<TreeView>("TreeView");
        Add<TreeViewItem>("TreeViewItem", p => { p["Header"] = Set(HeaderedItemsControl.HeaderProperty, value => (object?)value.GetString()); p["IsExpanded"] = Set(TreeViewItem.IsExpandedProperty, value => value.GetBoolean()); }, Input(TreeViewItem.IsExpandedProperty, value => value));
        Add<NumericUpDown>("NumericUpDown", p =>
        {
            p["Value"] = Set(NumericUpDown.ValueProperty, value => value.ValueKind == JsonValueKind.Null ? null : (decimal?)value.GetDecimal());
            p["Minimum"] = Set(NumericUpDown.MinimumProperty, value => value.GetDecimal()); p["Maximum"] = Set(NumericUpDown.MaximumProperty, value => value.GetDecimal());
            p["Increment"] = Set(NumericUpDown.IncrementProperty, value => value.GetDecimal()); p["FormatString"] = Set(NumericUpDown.FormatStringProperty, value => value.GetString()!);
        }, Input(NumericUpDown.ValueProperty, value => value));
        Add<DatePicker>("DatePicker", p =>
        {
            p["SelectedDate"] = Set(DatePicker.SelectedDateProperty, NullableDate);
            p["DayVisible"] = Set(DatePicker.DayVisibleProperty, value => value.GetBoolean()); p["MonthVisible"] = Set(DatePicker.MonthVisibleProperty, value => value.GetBoolean()); p["YearVisible"] = Set(DatePicker.YearVisibleProperty, value => value.GetBoolean());
        }, Input(DatePicker.SelectedDateProperty, value => value?.ToString("O", CultureInfo.InvariantCulture)));
        Add<CalendarDatePicker>("CalendarDatePicker", p => { p["SelectedDate"] = Set(CalendarDatePicker.SelectedDateProperty, value => NullableDate(value)?.DateTime); p["PlaceholderText"] = Set(CalendarDatePicker.PlaceholderTextProperty, value => value.GetString()); }, Input(CalendarDatePicker.SelectedDateProperty, value => value?.ToString("O", CultureInfo.InvariantCulture)));
        Add<Calendar>("Calendar", p => p["SelectedDate"] = Set(Calendar.SelectedDateProperty, value => NullableDate(value)?.DateTime), Input(Calendar.SelectedDateProperty, value => value?.ToString("O", CultureInfo.InvariantCulture)));
        Add<TimePicker>("TimePicker", p =>
        {
            p["SelectedTime"] = Set(TimePicker.SelectedTimeProperty, value => value.ValueKind == JsonValueKind.Null ? null : (TimeSpan?)TimeSpan.Parse(value.GetString()!, CultureInfo.InvariantCulture));
            p["MinuteIncrement"] = Set(TimePicker.MinuteIncrementProperty, Integer); p["ClockIdentifier"] = Set(TimePicker.ClockIdentifierProperty, value => value.GetString()!);
        }, Input(TimePicker.SelectedTimeProperty, value => value?.ToString("c", CultureInfo.InvariantCulture)));
        Add<Rectangle>("Rectangle", p => { Shape(p); p["RadiusX"] = Set(Rectangle.RadiusXProperty, Number); p["RadiusY"] = Set(Rectangle.RadiusYProperty, Number); });
        Add<Ellipse>("Ellipse", Shape); Add<Line>("Line", p => { Shape(p); p["StartPoint"] = Set(Line.StartPointProperty, PointValue); p["EndPoint"] = Set(Line.EndPointProperty, PointValue); });
        return new(UiAvaloniaFeatureCatalog.Extend(entries));
    }
    private static Func<Control, AvaloniaPropertyChangedEventArgs, JsonElement?> Input<T, TResult>(AvaloniaProperty<T> property, Func<T, TResult> convert) =>
        (control, args) => args.Property == property ? JsonSerializer.SerializeToElement(convert((T)control.GetValue(property)!)) : null;
    private static Action<Control, JsonElement?> Set<T>(AvaloniaProperty<T> property, Func<JsonElement, T> convert) => (control, value) =>
    { if (value.HasValue) control.SetValue(property, convert(value.Value)); else control.ClearValue(property); };
    private static double Number(JsonElement value) => (double)value.GetDecimal();
    private static int Integer(JsonElement value)
    {
        var number = value.GetDecimal();
        return number == decimal.Truncate(number) ? checked((int)number) : throw new UiException("invalid_property", "Integer property required.");
    }
    private static double[] Tuple(JsonElement value) => value.ValueKind == JsonValueKind.Number ? [Number(value)] : value.GetString()!.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    private static Thickness ThicknessValue(JsonElement value)
    {
        var parts = Tuple(value); return parts.Length switch { 1 => new(parts[0]), 2 => new(parts[0], parts[1]), 4 => new(parts[0], parts[1], parts[2], parts[3]), _ => throw new UiException("invalid_property", "Invalid thickness.") };
    }
    private static CornerRadius CornerRadiusValue(JsonElement value)
    {
        var parts = Tuple(value); return parts.Length switch { 1 => new(parts[0]), 2 => new(parts[0], parts[1], parts[0], parts[1]), 4 => new(parts[0], parts[1], parts[2], parts[3]), _ => throw new UiException("invalid_property", "Invalid corner radius.") };
    }
    private static Point PointValue(JsonElement value) { var parts = Tuple(value); return new(parts[0], parts[1]); }
    private static DateTimeOffset? NullableDate(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static IBrush? Brush(JsonElement value) => new SolidColorBrush(Color.Parse(value.GetString()!));
}
