using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>A validated reference to one property of the actual templated parent.</summary>
public sealed record UiTemplateBinding(string Property, string Mode = "OneWay");
public sealed record UiControlTemplateNode(string Type, ImmutableDictionary<string, JsonElement> Properties,
    ImmutableDictionary<string, UiTemplateBinding> Bindings, ImmutableArray<UiControlTemplateNode> Children)
{
    public ImmutableArray<UiStyleRule> Styles { get; init; } = [];
}
/// <summary>Inert template instructions. No CLR type name, delegate or XAML loader is transported.</summary>
public sealed record UiControlTemplate(string TargetType, UiControlTemplateNode Root);
public sealed record UiControlTheme(string TargetType, ImmutableDictionary<string, JsonElement> Properties,
    UiControlTemplate? Template, ImmutableArray<UiStyleRule> Styles);

/// <summary>Source, transport and native templates share the same property and structural checks.</summary>
public static class UiControlTemplates
{
    public const int MaximumNodes = 512;
    public const int MaximumDepth = 24;
    public static bool Supports(string type) => UiAvaloniaFeatureSchema.TemplatedControls.Contains(type, StringComparer.Ordinal);
    public static int Validate(UiControlTemplate? template, string targetType, UiCatalog catalog)
    {
        if (template is null) return 0;
        if (template.TargetType != targetType || !Supports(targetType)) throw Invalid("Template target is not its registered templated owner.");
        var owner = catalog.Components[targetType]; var names = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        void Check(UiControlTemplateNode node, int depth)
        {
            if (node is null || ++count > MaximumNodes || depth > MaximumDepth || node.Properties is null || node.Bindings is null || node.Children.IsDefault)
                throw Invalid("Invalid or oversized control template.");
            if (!catalog.Components.TryGetValue(node.Type, out var component)) throw Invalid("Template control is not registered.");
            if (node.Type == "ItemsPresenter" && targetType is not ("ItemsControl" or "ListBox" or "ComboBox" or "TabControl" or "TreeView" or "TreeViewItem"))
                throw Invalid("ItemsPresenter requires an items owner.");
            if (node.Properties.ContainsKey("UseRenderTransform") || node.Bindings.ContainsKey("UseRenderTransform"))
                throw Invalid("Template transforms use LayoutTransform directly, not the global render bridge.");
            if (node.Properties.TryGetValue("Name", out var name) && !names.Add(name.GetString()!)) throw Invalid("Duplicate template-part name.");
            var children = node.Children.Select(child => new UiElement("part", child?.Type ?? "", ImmutableDictionary<string, JsonElement>.Empty, [])).ToImmutableArray();
            UiTreeValidation.ValidateElement(new("template", node.Type, node.Properties, children), component);
            UiStyles.Validate(node.Styles, catalog);
            foreach (var pair in node.Bindings)
            {
                if (pair.Value is null || !component.Properties.TryGetValue(pair.Key, out var destination) ||
                    !owner.Properties.TryGetValue(pair.Value.Property, out var source) || node.Properties.ContainsKey(pair.Key))
                    throw Invalid("TemplateBinding requires registered, nonconflicting properties.");
                if (pair.Key is "Name" or "Classes" || pair.Value.Property is "Name" or "Classes" || pair.Key.Contains('.') || pair.Value.Property.Contains('.'))
                    throw Invalid("TemplateBinding requires a single native property, not a path or collection.");
                if (source.Kind != destination.Kind) throw Invalid("TemplateBinding property types disagree.");
                if (pair.Value.Mode is not ("OneWay" or "TwoWay")) throw Invalid("TemplateBinding supports OneWay and TwoWay.");
                if (pair.Value.Mode == "TwoWay" && (pair.Key != component.InputProperty || pair.Value.Property != owner.InputProperty))
                    throw Invalid("TwoWay template binding is restricted to the registered input pair.");
                if (node.Children.Length > 0 && pair.Key is "Content" or "ItemsSource") throw Invalid("Template content binding conflicts with children.");
            }
            foreach (var child in node.Children) Check(child, depth + 1);
        }
        Check(template.Root, 0); return count;
    }
    public static int Validate(UiControlTheme? theme, string targetType, UiCatalog catalog)
    {
        if (theme is null) return 0;
        if (theme.TargetType != targetType || !Supports(targetType) || theme.Properties is null) throw Invalid("ControlTheme target is not its owner.");
        var component = catalog.Components[targetType];
        foreach (var property in theme.Properties.Keys) UiStyles.ValidateProperty(component, property);
        UiTreeValidation.ValidateElement(new("theme", targetType, theme.Properties, []), component);
        UiStyles.Validate(theme.Styles, catalog);
        if (theme.Styles.Any(rule => UiStyles.ParseSelector(rule.Selector).Target != targetType)) throw Invalid("Nested theme styles must target the theme owner.");
        return Validate(theme.Template, targetType, catalog);
    }
    public static bool Equivalent<T>(T? left, T? right) where T : class => ReferenceEquals(left, right) ||
        left is not null && right is not null && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));
    private static UiException Invalid(string text) => new("invalid_template", text);
}
