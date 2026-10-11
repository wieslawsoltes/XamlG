namespace XamlG.IntelligentUI;

/// <summary>Typed input, keyboard and accessibility contracts. These properties do not
/// participate in presentation styles or acquire independent action authority.</summary>
internal static class UiInputFeatureSchema
{
    internal static IEnumerable<UiComponent> Extend(IEnumerable<UiComponent> source)
    {
        var components = source.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var boolean = new UiProperty(UiPropertyKind.Boolean);
        var text = new UiProperty(UiPropertyKind.Text);
        void Add(string name, params (string Name, UiProperty Value)[] properties)
        {
            var component = components[name];
            components[name] = component with { Properties = component.Properties.SetItems(properties.Select(p => new KeyValuePair<string, UiProperty>(p.Name, p.Value))) };
        }
        foreach (var name in components.Keys.ToArray())
            Add(name, ("IsTabStop", boolean), ("TabIndex", new(UiPropertyKind.Integer, 0, 32767)),
                ("KeyboardNavigation.TabNavigation", new(UiPropertyKind.Choice, Choices: ["Continue", "Cycle", "Contained", "Once", "None", "Local"])),
                ("AutomationProperties.AutomationId", text), ("AutomationProperties.HelpText", text));
        foreach (var name in new[] { "Button", "RepeatButton", "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch" })
            Add(name, ("IsDefault", boolean), ("IsCancel", boolean));
        foreach (var name in new[] { "CheckBox", "ToggleButton", "RadioButton", "ToggleSwitch" })
            Add(name, ("IsChecked", new(UiPropertyKind.Boolean, Nullable: true)), ("IsThreeState", boolean));
        Add("TextBox", ("CaretIndex", new(UiPropertyKind.Integer, 0, 16384)),
            ("SelectionStart", new(UiPropertyKind.Integer, 0, 16384)), ("SelectionEnd", new(UiPropertyKind.Integer, 0, 16384)));
        Add("Slider", ("SmallChange", new(UiPropertyKind.Number, 0, 1000000)),
            ("LargeChange", new(UiPropertyKind.Number, 0, 1000000)), ("IsDirectionReversed", boolean));
        Add("RepeatButton", ("Delay", new(UiPropertyKind.Integer, 0, 60000)),
            ("Interval", new(UiPropertyKind.Integer, 16, 60000)));
        return components.Values;
    }
}
