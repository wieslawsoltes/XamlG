namespace XamlG.IntelligentUI;

/// <summary>Cross-property checks shared by interpreted and transport-provided resolved trees.</summary>
public static class UiTreeValidation
{
    public static void ValidateElement(UiElement node, UiComponent component, int textLimit = 16384)
    {
        if (node.Children.IsDefault || node.Properties == null || node.Children.Length > component.MaximumChildren ||
            node.Children.Any(child => child is null) ||
            component.ChildTypes is { } allowed && node.Children.Any(child => !allowed.Contains(child.Type, StringComparer.Ordinal))) throw new UiException("invalid_content", "Invalid child container.");
        if (node.ActionId != null && !component.SupportsAction || node.StateKey != null && component.InputProperty == null) throw new UiException("invalid_tree", "Undeclared input or action capability.");
        var properties = node.Properties;
        foreach (var property in properties)
        {
            if (!component.Properties.TryGetValue(property.Key, out var definition)) throw new UiException("unknown_property", "Undeclared property.");
            definition.ValidateValue(property.Value);
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String && property.Value.GetString()!.Length > textLimit) throw new UiException("text_limit", "Computed property text exceeds the limit.");
        }
        UiDrawingValues.Validate(node);
        foreach (var axis in new[] { "Width", "Height" })
            if (properties.TryGetValue("Min" + axis, out var minimum) && properties.TryGetValue("Max" + axis, out var maximum) && minimum.GetDecimal() > maximum.GetDecimal())
                throw new UiException("invalid_property", "Minimum size must not exceed maximum size.");
        if (node.Children.Length != 0 && (properties.ContainsKey("ItemsSource") || properties.ContainsKey("Content"))) throw new UiException("invalid_content", "Child elements conflict with Content or ItemsSource.");
        // Enforce this in the resolved tree, not only in ChangeState: declared local
        // patches, streaming publications, tool data and transport snapshots share it.
        if (node.Type == "TextBox" && properties.TryGetValue("MaxLength", out var length) &&
            properties.TryGetValue("Text", out var text) && text.GetString()!.Length > length.GetDecimal())
            throw new UiException("invalid_property", "Text exceeds the input's declared MaxLength.");
        if (node.Type is "Slider" or "ProgressBar" or "NumericUpDown")
        {
            var min = properties.TryGetValue("Minimum", out var lower) ? lower.GetDecimal() : node.Type == "NumericUpDown" ? -1000000 : 0;
            var max = properties.TryGetValue("Maximum", out var upper) ? upper.GetDecimal() : node.Type == "NumericUpDown" ? 1000000 : 100;
            var value = properties.TryGetValue("Value", out var current) && current.ValueKind != System.Text.Json.JsonValueKind.Null ? current.GetDecimal() : (decimal?)null;
            if (min >= max || value is { } number && (number < min || number > max)) throw new UiException("invalid_range", "Range requires Minimum < Maximum and Value within that range.");
        }
        if (properties.TryGetValue("SelectedIndex", out var selected))
        {
            var count = properties.TryGetValue("ItemsSource", out var items) ? items.GetArrayLength() : node.Children.Length;
            if (selected.GetDecimal() >= count) throw new UiException("invalid_selection", "SelectedIndex is outside the current items.");
        }
        foreach (var property in properties.Where(p => p.Key is "ColumnDefinitions" or "RowDefinitions")) ValidateDefinitions(property.Value.GetString()!);
        if (properties.TryGetValue("FormatString", out var format)) ValidateFormat(format.GetString()!);
    }
    private static void ValidateFormat(string format)
    {
        if (format.Length > 64 || format.Length > 1 && char.IsAsciiLetter(format[0]) && format.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 &&
            (!int.TryParse(format.AsSpan(1), out var precision) || precision > 16)) throw new UiException("invalid_format", "Numeric format strings are limited to 64 characters and precision 16.");
    }
    private static void ValidateDefinitions(string source)
    {
        var parts = source.Split(',');
        if (parts.Length > 64) throw new UiException("invalid_property", "At most 64 grid definitions are allowed.");
        foreach (var raw in parts)
        {
            var value = raw.Trim(); if (value is "Auto" or "*") continue;
            if (value.EndsWith('*')) value = value[..^1];
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0 || number > 10000) throw new UiException("invalid_property", "Invalid grid definition.");
        }
    }
}
