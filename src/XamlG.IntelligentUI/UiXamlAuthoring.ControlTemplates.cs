using System.Collections.Immutable;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed partial class UiXamlAuthoring
{
    private void NormalizeControlAuthoring(XElement owner, Scope scope, string path, int depth)
    {
        foreach (var name in new[] { "Template", "Theme" })
        {
            var properties = Properties(owner, name); var attribute = owner.Attribute(name);
            if (properties.Length == 0 && attribute == null) continue;
            if (!UiControlTemplates.Supports(owner.Name.LocalName) || owner.Name.Namespace != Ns || properties.Length > 1 || properties.Length != 0 && attribute != null)
                throw Error("invalid_template", "Template and Theme require a single declaration on a registered templated control.");
            var (value, declaration, resource) = ControlAuthoringValue(attribute, properties, scope);
            if (resource != null && (!_resolving.Add(resource) || _resolving.Count > limits.Depth)) throw Error("resource_cycle", "Control template/theme cycle.");
            try
            {
                if (name == "Template") owner.AddAnnotation(ReadControlTemplate(value, owner.Name.LocalName, declaration, path, depth));
                else owner.AddAnnotation(ReadControlTheme(value, owner.Name.LocalName, declaration, path, depth));
            }
            finally { if (resource != null) _resolving.Remove(resource); }
            attribute?.Remove(); foreach (var property in properties) property.Remove();
        }
    }
    private (XElement Value, Scope Declaration, Resource? Resource) ControlAuthoringValue(XAttribute? attribute, XElement[] properties, Scope scope)
    {
        if (attribute != null && properties.Length != 0 || attribute == null && properties.Length != 1)
            throw Error("invalid_template", "A template/theme requires exactly one value.");
        Resource? resource = null; XElement value;
        if (attribute != null)
        {
            const string prefix = "{StaticResource ";
            if (!attribute.Value.StartsWith(prefix, StringComparison.Ordinal) || !attribute.Value.EndsWith('}')) throw Error("invalid_template", "Use a static template/theme resource reference.");
            resource = Find(scope, attribute.Value[prefix.Length..^1].Trim()); value = resource.Node;
        }
        else
        {
            var property = properties.Single(); CheckAttributes(property); CheckText(property);
            if (property.Elements().Count() != 1) throw Error("invalid_template", "Expected one template/theme value.");
            value = property.Elements().Single();
            if (value.Name == Ns + "StaticResource")
            {
                CheckAttributes(value, "ResourceKey"); CheckText(value);
                if (value.HasElements) throw Error("invalid_template", "Invalid template resource reference.");
                resource = Find(scope, Required(value, "ResourceKey")); value = resource.Node;
            }
        }
        return (value, resource?.Scope ?? scope, resource);
    }
    private string ControlTarget(XElement source, string? owner = null)
    {
        var value = (string?)source.Attribute("TargetType") ?? owner ?? throw Error("invalid_template", "A reusable template needs TargetType.");
        if (value.StartsWith("{x:Type ", StringComparison.Ordinal) && value.EndsWith('}')) value = value[8..^1].Trim();
        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 2 || source.GetNamespaceOfPrefix(parts[0]) != Ns) throw Error("invalid_template", "TargetType must be in the Avalonia namespace.");
            value = parts[1];
        }
        if (!UiControlTemplates.Supports(value) || owner != null && owner != value) throw Error("invalid_template", "Template/theme target differs from its owner.");
        return value;
    }
    private UiControlTemplateSource ReadControlTemplate(XElement source, string? target, Scope scope, string path, int depth)
    {
        if (source.Name != Ns + "ControlTemplate") throw Error("invalid_template", "Expected ControlTemplate.");
        CheckAttributes(source, "TargetType"); CheckText(source); var type = ControlTarget(source, target);
        if (source.Elements().Count() != 1 || source.Elements().Single().Name.Namespace != Ns || !IsVisual(source.Elements().Single()))
            throw Error("invalid_template", "A control template requires one native visual root.");
        var root = new XElement(source.Elements().Single());
        Visit(root, scope, path + ".control-template", depth + 1);
        return new(type, root);
    }
    private UiControlThemeSource ReadControlTheme(XElement source, string? target, Scope scope, string path, int depth)
    {
        if (source.Name != Ns + "ControlTheme" || depth > limits.Depth) throw Error("invalid_template", "Expected a bounded ControlTheme.");
        CheckAttributes(source, "TargetType", "BasedOn"); CheckText(source); var type = ControlTarget(source, target);
        var values = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var rules = ImmutableArray.CreateBuilder<XElement>(); UiControlTemplateSource? template = null;
        if (source.Attribute("BasedOn") is { } basedOn)
        {
            var (basis, declaration, resource) = ControlAuthoringValue(basedOn, [], scope);
            if (resource == null || !_resolving.Add(resource) || _resolving.Count > limits.Depth) throw Error("resource_cycle", "ControlTheme inheritance cycle.");
            try
            {
                var parent = ReadControlTheme(basis, type, declaration, path + ".base", depth + 1);
                foreach (var setter in parent.Setters.Elements()) values[(string)setter.Attribute("Property")!] = new XElement(setter);
                template = parent.Template; rules.AddRange(parent.Styles);
            }
            finally { _resolving.Remove(resource); }
        }
        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in source.Elements())
        {
            if (child.Name == Ns + "Style") { ReadStyle(child, type, scope, rules); continue; }
            if (child.Name != Ns + "Setter") throw Error("invalid_template", "ControlTheme accepts typed setters and nested styles.");
            CheckAttributes(child, "Property", "Value"); CheckText(child);
            var name = Required(child, "Property"); if (!local.Add(name)) throw Error("invalid_template", "Duplicate theme setter.");
            if (name == "Template")
            {
                var wrappers = child.Elements().ToArray();
                if (wrappers.Any(element => element.Name != Ns + "Setter.Value")) throw Error("invalid_template", "Expected Setter.Value.");
                var (value, declaration, resource) = ControlAuthoringValue(child.Attribute("Value"), wrappers, scope);
                if (resource != null && (!_resolving.Add(resource) || _resolving.Count > limits.Depth)) throw Error("resource_cycle", "Template/theme resource cycle.");
                try { template = ReadControlTemplate(value, type, declaration, path, depth + 1); }
                finally { if (resource != null) _resolving.Remove(resource); }
            }
            else
            {
                UiStyles.ValidateProperty(catalog.Components[type], name);
                var holder = new XElement(Ns + "Style", new XAttribute("Selector", type), new XElement(child));
                var flattened = ImmutableArray.CreateBuilder<XElement>(); ReadStyle(holder, null, scope, flattened);
                values[name] = flattened.Single().Elements().Single();
            }
        }
        if (values.Count > UiStyles.MaximumSetters || rules.Count > UiStyles.MaximumRules) throw Error("invalid_template", "Theme budget exceeded.");
        return new(type, new XElement(Ns + "Setters", values.Values), template, rules.ToImmutable());
    }
    private void ValidateUnusedControlAuthoring(Resource resource)
    {
        if (!_resolving.Add(resource) || _resolving.Count > limits.Depth) throw Error("resource_cycle", "Template/theme resource cycle.");
        try
        {
            var holder = new XElement(Ns + ControlTarget(resource.Node));
            if (resource.Node.Name == Ns + "ControlTemplate") holder.AddAnnotation(ReadControlTemplate(resource.Node, null, resource.Scope, "unused", 0));
            else holder.AddAnnotation(ReadControlTheme(resource.Node, null, resource.Scope, "unused", 0));
            validate?.Invoke(holder);
        }
        finally { _resolving.Remove(resource); }
    }
}
