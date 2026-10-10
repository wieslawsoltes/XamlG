using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed class UiTemplate
{
    private readonly UiPlanNode _root;
    private readonly UiLimits _limits;
    private readonly UiCatalog _catalog;
    private readonly string _templateId = Guid.NewGuid().ToString("N");
    internal UiTemplate(UiPlanNode root, UiLimits limits, UiCatalog catalog) { _root = root; _limits = limits; _catalog = catalog; }
    public ImmutableArray<UiElement> Render(JsonElement state, JsonElement data, IEnumerable<UiElement>? previous = null)
    {
        var count = 0; var keys = new HashSet<string>(StringComparer.Ordinal);
        var resolved = RenderNode(_root, state, data, null, "", keys, ref count);
        var lowered = UiCompositeCatalog.Lower(resolved, _catalog, _limits);
        lowered = UiFormSemantics.Apply(resolved, lowered, _catalog);
        return UiFormProjection.Apply(lowered, previous, state, data, _templateId, _catalog, _limits);
    }
    private UiControlTemplate? ResolveControlTemplate(UiPlanControlTemplate? template, JsonElement state, JsonElement data, JsonElement? item)
    {
        if (template is null) return null;
        UiControlTemplateNode Resolve(UiPlanControlTemplateNode part) => new(part.Type,
            part.Properties.ToImmutableDictionary(p => p.Key, p => _catalog.Components[part.Type].Properties[p.Key].Validate(p.Value.Resolve(state, data, item)), StringComparer.Ordinal),
            part.Bindings, part.Children.Select(Resolve).ToImmutableArray())
        { Styles = ResolveStyles(part.Styles, state, data, item) };
        return new(template.Target, Resolve(template.Root));
    }
    private static ImmutableArray<UiStyleRule> ResolveStyles(ImmutableArray<UiPlanStyle> styles, JsonElement state, JsonElement data, JsonElement? item) =>
        styles.Select(style => new UiStyleRule(style.Selector, style.Properties.ToImmutableDictionary(
            p => p.Key, p => style.Target.Properties[p.Key].Validate(p.Value.Resolve(state, data, item)), StringComparer.Ordinal))).ToImmutableArray();
    private UiControlTheme? ResolveControlTheme(UiPlanControlTheme? theme, JsonElement state, JsonElement data, JsonElement? item) => theme is null ? null :
        new(theme.Target, theme.Properties.ToImmutableDictionary(p => p.Key, p => _catalog.Components[theme.Target].Properties[p.Key].Validate(p.Value.Resolve(state, data, item)), StringComparer.Ordinal),
            ResolveControlTemplate(theme.Template, state, data, item), ResolveStyles(theme.Styles, state, data, item));
    private ImmutableArray<UiElement> RenderNode(UiPlanNode node, JsonElement state, JsonElement data, JsonElement? item, string scope, HashSet<string> keys, ref int count, bool expanded = false)
    {
        if (node.Each != null && !expanded)
        {
            var items = node.Each.Evaluate(state, data, item);
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > _limits.Nodes) throw new UiException("invalid_repeat", "Repeat source must be a bounded JSON array.");
            var result = ImmutableArray.CreateBuilder<UiElement>();
            foreach (var child in items.EnumerateArray())
            {
                var identity = node.ItemKey!.Evaluate(state, data, child);
                if (identity.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new UiException("invalid_repeat", "Item keys must be strings or numbers.");
                var key = UiJson.Text(UiJson.Value(identity));
                if (key.Length is 0 or > 80) throw new UiException("invalid_repeat", "Invalid item key length.");
                result.AddRange(RenderNode(node, state, data, child, scope + "/" + node.Key + "[" + Uri.EscapeDataString(key) + "]", keys, ref count, true));
            }
            return result.ToImmutable();
        }
        if (node.Context != null) item = node.Context.Resolve(state, data, item);
        if (node.When != null && !UiExpression.Bool(UiJson.Value(node.When.Evaluate(state, data, item)))) return [];
        if (++count > _limits.Nodes) throw new UiException("node_limit", "Expanded UI exceeds the node limit.");
        var nodeKey = scope + "/" + node.Key;
        if (!keys.Add(nodeKey)) throw new UiException("duplicate_key", "Duplicate rendered item key: " + nodeKey);
        var properties = node.Properties.ToImmutableDictionary(p => p.Key, p => node.Component.Properties[p.Key].Validate(p.Value.Resolve(state, data, item)), StringComparer.Ordinal);
        if (node.StateKey != null)
        {
            if (!state.TryGetProperty(node.StateKey, out var value)) throw new UiException("invalid_binding", "Missing state: " + node.StateKey);
            properties = properties.SetItem(node.Component.InputProperty!, node.Component.Properties[node.Component.InputProperty!].Validate(value));
        }
        var children = ImmutableArray.CreateBuilder<UiElement>();
        foreach (var child in node.Children) children.AddRange(RenderNode(child, state, data, item, scope, keys, ref count));
        var element = new UiElement(nodeKey, node.Component.Name, properties, children.ToImmutable(), node.StateKey, node.ActionId)
        { ActionItem = node.ActionId == null ? null : item,
            ControlTemplate = ResolveControlTemplate(node.ControlTemplate, state, data, item),
            ControlTheme = ResolveControlTheme(node.ControlTheme, state, data, item),
            Styles = node.Styles.Select(style => new UiStyleRule(style.Selector, style.Properties.ToImmutableDictionary(
                p => p.Key, p => style.Target.Properties[p.Key].Validate(p.Value.Resolve(state, data, item)), StringComparer.Ordinal))).ToImmutableArray() };
        count += UiControlTemplates.Validate(element.ControlTemplate, element.Type, _catalog);
        count += UiControlTemplates.Validate(element.ControlTheme, element.Type, _catalog);
        if (count > _limits.Nodes) throw new UiException("node_limit", "Instantiated control templates exceed the surface node budget.");
        UiStyles.Validate(element.Styles, _catalog, _limits.TextCharacters);
        UiTreeValidation.ValidateElement(element, node.Component, _limits.TextCharacters);
        return [element];
    }
}
