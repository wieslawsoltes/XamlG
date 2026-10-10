using System.Text.Json;

namespace XamlG.IntelligentUI;

internal sealed partial class UiCompositeLowerer
{
    private sealed record FieldError(string Label, string Message);
    private sealed record FormScope(Dictionary<string, FieldError> Errors, string? Error, bool ShowErrors)
    {
        internal bool IsValid => Error == null && Errors.Count == 0;
    }
    private FormScope? _form;
    private static bool Active(UiElement node) => Flag(node, "IsVisible", true) && Flag(node, "IsEnabled", true);
    private static string ErrorMessage(UiElement node, string fallback)
    {
        var message = Text(node, "ErrorText");
        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    private FieldError? ValidateField(UiElement field)
    {
        if (field.Children.Length != 1 || !catalog.Components.TryGetValue(field.Children[0].Type, out var component) || component.InputProperty == null)
            throw new UiException("invalid_form", "A Field must contain exactly one registered native input.");
        var input = field.Children[0];
        var missing = Flag(field, "IsRequired", false) &&
            (!input.Properties.TryGetValue(component.InputProperty, out var value) || !HasRequiredValue(value, component.InputProperty));
        if (!missing && Flag(field, "IsValid", true)) return null;
        return new(Text(field, "Label", input.StateKey ?? "Value"),
            ErrorMessage(field, missing ? "A value is required." : "The value is invalid."));
    }

    private static bool HasRequiredValue(JsonElement value, string property) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number when property == "SelectedIndex" => value.GetDecimal() >= 0,
        JsonValueKind.Array => value.GetArrayLength() != 0,
        _ => true
    };

    private FormScope FormValidation(UiElement form)
    {
        var errors = new Dictionary<string, FieldError>(StringComparer.Ordinal);
        void Visit(UiElement node)
        {
            if (!Active(node)) return;
            if (node.Type == "ui:Field")
            {
                var error = ValidateField(node);
                if (error != null && Active(node.Children[0])) errors.Add(node.Key, error);
                return;
            }
            if (node.Type is "Expander" or "TreeViewItem" && !Flag(node, "IsExpanded", false)) return;
            if (node.Type == "TabControl")
            {
                var index = (int)Number(node, "SelectedIndex", 0);
                if (index >= 0 && index < node.Children.Length) Visit(node.Children[index]);
                return;
            }
            foreach (var child in node.Children) Visit(child);
        }
        if (Active(form)) foreach (var child in form.Children) Visit(child);
        return new(errors, Flag(form, "IsValid", true) ? null : ErrorMessage(form, "Review this form before submitting."), Flag(form, "ShowErrors", true));
    }
}
