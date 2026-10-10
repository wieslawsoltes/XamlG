using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XamlG.IntelligentUI;

internal static class UiFormProjection
{
    internal static IEnumerable<UiElement> Active(IEnumerable<UiElement> roots)
    {
        foreach (var node in roots)
        {
            if (node.Properties.TryGetValue("IsVisible", out var visible) && !visible.GetBoolean() ||
                node.Properties.TryGetValue("IsEnabled", out var enabled) && !enabled.GetBoolean()) continue;
            yield return node;
            if (node.Type is "Expander" or "TreeViewItem" && (!node.Properties.TryGetValue("IsExpanded", out var expanded) || !expanded.GetBoolean())) continue;
            if (node.Type == "TabControl")
            {
                var index = node.Properties.TryGetValue("SelectedIndex", out var selected) ? (int)selected.GetDecimal() : 0;
                if (index >= 0 && index < node.Children.Length)
                    foreach (var child in Active([node.Children[index]])) yield return child;
            }
            else foreach (var child in Active(node.Children)) yield return child;
        }
    }

    internal static ImmutableArray<UiElement> Apply(ImmutableArray<UiElement> roots,
        IEnumerable<UiElement>? previous, JsonElement state, JsonElement data, string templateId, UiCatalog catalog, UiLimits limits)
    {
        var nodes = Active(roots).ToArray();
        if (!nodes.Any(node => node.Form?.Role == "form")) return roots;
        var old = previous == null ? new Dictionary<string, UiFormInteraction>(StringComparer.Ordinal)
            : UiSessionStore.Flatten(previous).Where(node => node.FormState != null)
                .ToDictionary(node => node.FormState!.Id, node => node.FormState!, StringComparer.Ordinal);
        var stamp = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(templateId + "\n" + state.GetRawText() + "\n" + data.GetRawText())));
        var forms = new Dictionary<string, UiFormInteraction>(StringComparer.Ordinal);
        foreach (var root in nodes.Where(node => node.Form?.Role == "form"))
        {
            var declaration = root.Form!;
            old.TryGetValue(declaration.Id, out var history);
            var fields = ImmutableArray.CreateBuilder<UiFieldInteraction>();
            foreach (var input in nodes.Where(node => node.Form is { Role: "input" } form && form.Id == declaration.Id))
            {
                var annotation = input.Form!;
                var inputProperty = catalog.Get(input.Type).InputProperty!;
                var value = input.Properties.TryGetValue(inputProperty, out var found) ? found : UiJson.Element(null);
                var prior = history?.Fields.FirstOrDefault(field => field.Key == input.Key && field.StateKey == input.StateKey);
                var initial = prior?.InitialValue ?? value;
                fields.Add(new(input.Key, input.StateKey, annotation.Label ?? input.StateKey ?? "Value", initial, value,
                    prior?.Touched ?? false, !JsonElement.DeepEquals(initial, value), annotation.Error));
            }
            var keepValidation = history?.Stamp == stamp && history.Validator == declaration.Validator;
            forms.Add(declaration.Id, new(declaration.Id, stamp, fields.ToImmutable(), history?.Submitted ?? false,
                keepValidation && history!.Pending, keepValidation && history!.Validated,
                keepValidation ? history!.AsyncError : null, declaration.Validator, declaration.Error,
                keepValidation ? history!.ValidationId : null));
        }
        var count = 0;
        UiElement Visit(UiElement node, int depth)
        {
            if (++count > limits.Nodes || depth > limits.Depth) throw new UiException("node_limit", "Form presentation exceeds tree limits.");
            var result = node with { Children = node.Children.Select(child => Visit(child, depth + 1)).ToImmutableArray() };
            if (node.Form is not { } annotation || !forms.TryGetValue(annotation.Id, out var form)) return result;
            bool Shown(UiFieldInteraction field) => annotation.ShowErrors && (annotation.ErrorMode == "Always" || form.Submitted || annotation.ErrorMode == "OnTouch" && field.Touched);
            if (annotation.Role == "form")
            {
                result = result with { FormState = form };
                if (form.Pending || form.AsyncError != null)
                {
                    if (++count > limits.Nodes || depth + 1 > limits.Depth) throw new UiException("node_limit", "Form feedback exceeds tree limits.");
                    var feedback = new UiElement(node.Key + "/@async-validation", "TextBlock",
                        ImmutableDictionary<string, JsonElement>.Empty.Add("Text", UiJson.Element(form.Pending ? "Validating…" : form.AsyncError!)), []);
                    UiTreeValidation.ValidateElement(feedback, catalog.Get("TextBlock"), limits.TextCharacters);
                    result = result with { Children = result.Children.Add(feedback) };
                }
            }
            else if (annotation.Role == "submit")
            {
                var synchronousValid = form.Error == null && form.Fields.All(field => field.Error == null);
                result = result with { Properties = result.Properties.SetItem("IsEnabled", UiJson.Element(annotation.AuthorEnabled && synchronousValid && !form.Pending)) };
            }
            else if (annotation.Role == "error")
            {
                var field = form.Fields.FirstOrDefault(field => field.Key == annotation.FieldKey);
                result = result with { Properties = result.Properties.SetItem("IsVisible", UiJson.Element(field != null && field.Error != null && Shown(field))) };
            }
            else if (annotation.Role == "summary")
            {
                var show = annotation.ShowErrors && (form.Error != null && (annotation.ErrorMode == "Always" || form.Submitted) || form.Fields.Any(field => field.Error != null && Shown(field)));
                result = result with { Properties = result.Properties.SetItem("IsVisible", UiJson.Element(show)) };
            }
            UiTreeValidation.ValidateElement(result, catalog.Get(result.Type), limits.TextCharacters);
            return result;
        }
        return roots.Select(root => Visit(root, 0)).ToImmutableArray();
    }

    internal static ImmutableArray<UiElement> ReplaceHistory(ImmutableArray<UiElement> roots, UiFormInteraction form)
        => roots.Select(node => node with
        {
            FormState = node.FormState?.Id == form.Id ? form : node.FormState,
            Children = ReplaceHistory(node.Children, form)
        }).ToImmutableArray();
}
