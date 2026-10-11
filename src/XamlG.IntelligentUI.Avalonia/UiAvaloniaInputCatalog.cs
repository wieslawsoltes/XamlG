using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Explicit native input adapters, including the nullable Boolean event contract.</summary>
internal static class UiAvaloniaInputCatalog
{
    internal static void Extend(Dictionary<string, UiControlRegistration> entries)
    {
        var shared = new Dictionary<string, Action<Control, JsonElement?>>(StringComparer.Ordinal)
        {
            ["IsTabStop"] = Set(InputElement.IsTabStopProperty, v => v.GetBoolean()),
            ["TabIndex"] = Set(InputElement.TabIndexProperty, Integer),
            ["KeyboardNavigation.TabNavigation"] = Set(KeyboardNavigation.TabNavigationProperty, v => Enum.Parse<KeyboardNavigationMode>(v.GetString()!)),
            ["AutomationProperties.AutomationId"] = Set(AutomationProperties.AutomationIdProperty, v => v.GetString()),
            ["AutomationProperties.HelpText"] = Set(AutomationProperties.HelpTextProperty, v => v.GetString())
        };
        foreach (var name in entries.Keys.ToArray())
            entries[name] = entries[name] with { Setters = entries[name].Setters.SetItems(shared) };
        void Add(string name, params (string Name, Action<Control, JsonElement?> Setter)[] setters) =>
            entries[name] = entries[name] with { Setters = entries[name].Setters.SetItems(setters.Select(s => new KeyValuePair<string, Action<Control, JsonElement?>>(s.Name, s.Setter))) };
        foreach (var name in new[] { "Button", "RepeatButton", "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch" })
            Add(name, ("IsDefault", Set(Button.IsDefaultProperty, v => v.GetBoolean())),
                ("IsCancel", Set(Button.IsCancelProperty, v => v.GetBoolean())));
        foreach (var name in new[] { "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch" })
        {
            Add(name, ("IsChecked", Set<bool?>(ToggleButton.IsCheckedProperty, v => v.ValueKind == JsonValueKind.Null ? null : v.GetBoolean())),
                ("IsThreeState", Set(ToggleButton.IsThreeStateProperty, v => v.GetBoolean())));
            entries[name] = entries[name] with
            {
                // JsonElement.Null is an input value, while a missing nullable JsonElement
                // means this property change is not an input event. Do not conflate the two.
                ReadInput = (control, change) => change.Property == ToggleButton.IsCheckedProperty
                    ? JsonSerializer.SerializeToElement(((ToggleButton)control).IsChecked) : null
            };
        }
        Add("TextBox", ("CaretIndex", Set(TextBox.CaretIndexProperty, Integer)),
            ("SelectionStart", Set(TextBox.SelectionStartProperty, Integer)), ("SelectionEnd", Set(TextBox.SelectionEndProperty, Integer)));
        Add("Slider", ("SmallChange", Set(RangeBase.SmallChangeProperty, Number)),
            ("LargeChange", Set(RangeBase.LargeChangeProperty, Number)), ("IsDirectionReversed", Set(Slider.IsDirectionReversedProperty, v => v.GetBoolean())));
        Add("RepeatButton", ("Delay", Set(RepeatButton.DelayProperty, Integer)), ("Interval", Set(RepeatButton.IntervalProperty, Integer)));
    }
    private static Action<Control, JsonElement?> Set<T>(AvaloniaProperty<T> property, Func<JsonElement, T> convert) => (control, value) =>
    {
        if (value is { } literal) control.SetValue(property, convert(literal)); else control.ClearValue(property);
    };
    private static int Integer(JsonElement value) => checked((int)value.GetDecimal());
    private static double Number(JsonElement value) => (double)value.GetDecimal();
}
