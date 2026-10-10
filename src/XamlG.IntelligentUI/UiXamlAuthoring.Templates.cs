using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed partial class UiXamlAuthoring
{
    private void ReadStyle(XElement source, string? parent, Scope scope, ImmutableArray<XElement>.Builder rules)
    {
        if (source.Name != Ns + "Style") throw Error("invalid_style", "Expected Style, not an include or executable extension.");
        CheckAttributes(source, "Selector"); CheckText(source);
        var groups = UiStyleSelectors.SplitGroups(Required(source, "Selector"));
        if (groups.Length > 1)
        {
            foreach (var group in groups)
            {
                var branch = new XElement(source); branch.SetAttributeValue("Selector", group);
                ReadStyle(branch, parent, scope, rules);
            }
            return;
        }
        var selector = groups[0];
        if (parent != null)
        {
            if (!selector.StartsWith('^')) throw Error("invalid_style", "Nested styles require the ^ selector.");
            selector = parent + selector[1..];
        }
        var parsed = UiStyles.ParseSelector(selector); UiStyleSelectors.ValidateTypes(parsed, catalog);
        var target = catalog.Get(parsed.Target);
        if (rules.Count >= UiStyles.MaximumRules) throw Error("invalid_style", "Style rule budget exceeded.");
        var rule = new XElement(Ns + "Style", new XAttribute("Selector", selector)); rules.Add(rule);
        foreach (var setter in source.Elements().Where(child => child.Name != Ns + "Style"))
        {
            if (setter.Name != Ns + "Setter") throw Error("invalid_style", "Expected a typed Setter.");
            CheckAttributes(setter, "Property", "Value"); CheckText(setter);
            var name = Required(setter, "Property"); UiStyles.ValidateProperty(target, name);
            if (rule.Elements().Count() >= UiStyles.MaximumSetters) throw Error("invalid_style", "Style setter budget exceeded.");
            var value = (string?)setter.Attribute("Value");
            if (setter.HasElements)
            {
                if (value != null || setter.Elements().Count() != 1 || setter.Elements().Single().Name != Ns + "Setter.Value")
                    throw Error("invalid_style", "Duplicate or invalid Setter.Value.");
                var property = setter.Elements().Single(); CheckAttributes(property); value = ScalarProperty(property, scope);
            }
            if (value == null) throw Error("invalid_style", "A setter requires a value.");
            rule.Add(new XElement(Ns + "Setter", new XAttribute("Property", name), new XAttribute("Value", Resolve(value, scope))));
        }
        foreach (var nested in source.Elements(Ns + "Style")) ReadStyle(nested, selector, scope, rules);
    }
    private void ValidateTemplate(XElement template)
    {
        CheckAttributes(template); CheckText(template);
        if (template.Elements().Count() != 1 || !IsVisual(template.Elements().Single()))
            throw Error("invalid_template", "DataTemplate requires one registered visual root.");
    }
    private void ExpandItemTemplate(XElement element, Scope scope, string path, int depth)
    {
        var property = Properties(element, "ItemTemplate"); var reference = element.Attribute("ItemTemplate");
        if (property.Length == 0 && reference == null) return;
        if (property.Length > 1 || property.Length != 0 && reference != null) throw Error("invalid_template", "Duplicate ItemTemplate.");
        if (element.Name.Namespace != Ns || element.Name.LocalName is not ("ItemsControl" or "ListBox" or "ComboBox"))
            throw Error("invalid_template", "ItemTemplate requires an ItemsControl, ListBox or ComboBox.");
        if (element.Elements().Any(child => !IsProperty(child))) throw Error("invalid_template", "ItemTemplate conflicts with explicit children.");
        var items = element.Attribute("ItemsSource") ?? throw Error("invalid_template", "Templated items require ItemsSource.");
        XElement template; Scope declaration = scope; Resource? resource = null;
        if (reference != null)
        {
            const string prefix = "{StaticResource "; var text = reference.Value;
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('}')) throw Error("invalid_template", "ItemTemplate requires a static template reference.");
            resource = Find(scope, text[prefix.Length..^1].Trim()); template = resource.Node; declaration = resource.Scope;
        }
        else
        {
            CheckAttributes(property[0]); CheckText(property[0]);
            if (property[0].Elements().Count() != 1) throw Error("invalid_template", "ItemTemplate requires one DataTemplate.");
            template = property[0].Elements().Single();
            if (template.Name == Ns + "StaticResource")
            {
                CheckAttributes(template, "ResourceKey"); CheckText(template);
                if (template.HasElements) throw Error("invalid_template", "Invalid template reference.");
                resource = Find(scope, Required(template, "ResourceKey")); template = resource.Node; declaration = resource.Scope;
            }
        }
        if (template.Name != Ns + "DataTemplate") throw Error("invalid_template", "Expected DataTemplate resource.");
        ValidateTemplate(template);
        if (resource != null && (!_resolving.Add(resource) || _resolving.Count > limits.Depth)) throw Error("resource_cycle", "Template reference cycle.");
        try
        {
            var child = new XElement(template.Elements().Single());
            if (child.DescendantsAndSelf().Count() + _expanded > limits.Nodes * 8) throw Error("node_limit", "Template expansion exceeds its budget.");
            var identity = (string?)element.Attribute(Ui + "ItemKey") ?? "{ui:Expr item}";
            if (child.Attribute(Ui + "Each") != null || child.Attribute(Ui + "ItemKey") != null)
                throw Error("invalid_template", "Put nested repetition inside the template root.");
            // Generated declaration keys have a private deterministic namespace. Inner row
            // identities remain provided by ItemKey, not inferred from an array index.
            var owner = (string?)element.Attribute(Ui + "Key") ?? (string?)element.Attribute(X + "Name") ?? path;
            var prefix = "tpl-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16] + ".";
            foreach (var node in child.DescendantsAndSelf())
            {
                var key = (string?)node.Attribute(Ui + "Key") ?? (string?)node.Attribute(X + "Name") ?? (string?)node.Attribute("Name");
                if (key != null) node.SetAttributeValue(Ui + "Key", prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]);
            }
            child.SetAttributeValue(Ui + "Key", prefix + "item");
            Visit(child, new Scope(declaration, "item"), path + ".template", depth + 1);
            var bindingScope = child.Annotation<UiBindingScope>()!;
            child.RemoveAnnotations<UiBindingScope>(); child.AddAnnotation(bindingScope with { RepeatRoot = scope.BindingRoot });
            child.SetAttributeValue(Ui + "Each", Resolve(items.Value, scope)); child.SetAttributeValue(Ui + "ItemKey", identity);
            child.AddAnnotation(new ExpandedTemplate());
            items.Remove(); element.Attribute(Ui + "ItemKey")?.Remove(); reference?.Remove();
            foreach (var wrapper in property) wrapper.Remove(); element.Add(child);
        }
        finally { if (resource != null) _resolving.Remove(resource); }
    }
    private sealed class ExpandedTemplate { }
}
