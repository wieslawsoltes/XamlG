using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Attaches declarative form roles after lowering without adding CLR capabilities.</summary>
internal static class UiFormSemantics
{
    internal static ImmutableArray<UiElement> Apply(ImmutableArray<UiElement> source, ImmutableArray<UiElement> lowered, UiCatalog catalog)
    {
        var roles = new Dictionary<string, UiFormAnnotation>(StringComparer.Ordinal);
        var active = UiFormProjection.Active(source).Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        static string Text(UiElement node, string name, string fallback = "") => node.Properties.TryGetValue(name, out var value) ? value.GetString()! : fallback;
        static bool Flag(UiElement node, string name, bool fallback = true) => node.Properties.TryGetValue(name, out var value) ? value.GetBoolean() : fallback;
        static string Error(UiElement node, string fallback) => string.IsNullOrWhiteSpace(Text(node, "ErrorText")) ? fallback : Text(node, "ErrorText");
        void Visit(UiElement node, UiFormAnnotation? form)
        {
            if (node.Type == "ui:Form")
            {
                var validator = Text(node, "Validator");
                if (validator.Length != 0) UiJson.Identifier(validator, "Validator name");
                form = new(node.Key, "form", Error: Flag(node, "IsValid") ? null : Error(node, "Review this form before submitting."),
                    ErrorMode: Text(node, "ErrorMode", "Always"), Validator: validator.Length == 0 ? null : validator, ShowErrors: Flag(node, "ShowErrors"));
                roles.Add(node.Key, form);
            }
            if (form != null)
            {
                if (node.Type == "ui:Field" && node.Children.Length == 1)
                {
                    var input = node.Children[0];
                    var property = catalog.Get(input.Type).InputProperty;
                    if (property != null)
                    {
                        var missing = Flag(node, "IsRequired", false) && (!input.Properties.TryGetValue(property, out var value) || !Required(value, property));
                        var error = active.Contains(node.Key) && active.Contains(input.Key) && (missing || !Flag(node, "IsValid"))
                            ? Error(node, missing ? "A value is required." : "The value is invalid.") : null;
                        var annotation = form with { Role = "input", FieldKey = input.Key, Label = Text(node, "Label", input.StateKey ?? "Value"),
                            Error = error, ShowErrors = form.ShowErrors && Flag(node, "ShowErrors") };
                        roles[input.Key] = annotation;
                        roles[node.Key + "/@error"] = annotation with { Role = "error" };
                    }
                }
                else if (node.Type == "ui:SubmitButton") roles[node.Key] = form with { Role = "submit", AuthorEnabled = Flag(node, "IsEnabled") };
                else if (node.Type == "ui:ValidationSummary")
                {
                    roles[node.Key] = form with { Role = "summary", ShowErrors = form.ShowErrors && Flag(node, "IsVisible") };
                    // Lowering preserves declaration order for active validation errors.
                    var errors = roles.Values.Where(role => role.Id == form.Id && role.Role == "input" && role.Error != null).ToArray();
                    for (var index = 0; index < errors.Length; index++) roles[node.Key + "/@field-" + index] = errors[index] with { Role = "error" };
                }
            }
            foreach (var child in node.Children) Visit(child, form);
        }
        foreach (var root in source) Visit(root, null);
        if (roles.Count == 0) return lowered;
        // Summaries may precede fields; populate their field roles after all declarations are collected.
        foreach (var pair in roles.Where(pair => pair.Value.Role == "summary").ToArray())
        {
            var errors = roles.Values.Where(role => role.Role == "input" && role.Id == pair.Value.Id && role.Error != null).ToArray();
            for (var index = 0; index < errors.Length; index++) roles[pair.Key + "/@field-" + index] = errors[index] with { Role = "error" };
        }
        UiElement Annotate(UiElement node) => node with { Form = roles.GetValueOrDefault(node.Key), Children = node.Children.Select(Annotate).ToImmutableArray() };
        return lowered.Select(Annotate).ToImmutableArray();
    }

    private static bool Required(JsonElement value, string property) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number when property == "SelectedIndex" => value.GetDecimal() >= 0,
        JsonValueKind.Array => value.GetArrayLength() != 0,
        _ => true
    };
}
