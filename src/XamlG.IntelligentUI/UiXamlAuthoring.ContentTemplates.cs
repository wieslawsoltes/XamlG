using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed partial class UiXamlAuthoring
{
    private void ExpandContentTemplate(XElement owner, Scope scope, string path, int depth)
    {
        var properties = Properties(owner, "ContentTemplate"); var reference = owner.Attribute("ContentTemplate");
        if (properties.Length == 0 && reference == null) return;
        if (properties.Length > 1 || properties.Length != 0 && reference != null) throw Error("invalid_template", "Duplicate ContentTemplate.");
        if (owner.Name.Namespace != Ns || !IsContentProperty(owner, "Content")) throw Error("invalid_template", "ContentTemplate requires a content control.");
        if (owner.Elements().Any(child => !IsProperty(child))) throw Error("invalid_template", "ContentTemplate conflicts with visual content.");
        var content = owner.Attribute("Content"); var contentProperties = Properties(owner, "Content");
        if (contentProperties.Length > 1 || content != null && contentProperties.Length != 0) throw Error("invalid_template", "Duplicate Content.");
        var value = content?.Value;
        if (contentProperties.Length == 1) { CheckAttributes(contentProperties[0]); value = ScalarProperty(contentProperties[0], scope); contentProperties[0].Remove(); }
        value ??= "{ui:Expr null}";
        var declaration = scope; Resource? resource = null; XElement template;
        if (reference != null)
        {
            const string prefix = "{StaticResource ";
            if (!reference.Value.StartsWith(prefix, StringComparison.Ordinal) || !reference.Value.EndsWith('}')) throw Error("invalid_template", "ContentTemplate requires a static template resource.");
            resource = Find(scope, reference.Value[prefix.Length..^1].Trim()); template = resource.Node; declaration = resource.Scope;
        }
        else
        {
            CheckAttributes(properties[0]); CheckText(properties[0]);
            if (properties[0].Elements().Count() != 1) throw Error("invalid_template", "ContentTemplate requires one DataTemplate.");
            template = properties[0].Elements().Single();
            if (template.Name == Ns + "StaticResource")
            {
                CheckAttributes(template, "ResourceKey"); CheckText(template);
                if (template.HasElements) throw Error("invalid_template", "Invalid template reference.");
                resource = Find(scope, Required(template, "ResourceKey")); template = resource.Node; declaration = resource.Scope;
            }
        }
        if (template.Name != Ns + "DataTemplate") throw Error("invalid_template", "Expected a DataTemplate.");
        ValidateTemplate(template);
        if (_resolving.Count >= limits.Depth || resource != null && !_resolving.Add(resource)) throw Error("resource_cycle", "Content template cycle.");
        try
        {
            var identity = (string?)owner.Attribute(Ui + "Key") ?? (string?)owner.Attribute("Name") ?? path;
            var prefix = "content-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16] + ".";
            var child = new XElement(template.Elements().Single());
            foreach (var node in child.DescendantsAndSelf())
            {
                var key = (string?)node.Attribute(Ui + "Key") ?? (string?)node.Attribute(X + "Name") ?? (string?)node.Attribute("Name");
                if (key != null) node.SetAttributeValue(Ui + "Key", prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16]);
            }
            child.SetAttributeValue(Ui + "Key", prefix + "root");
            // A private context holder preserves the owner's own bindings and the template
            // root's optional DataContext. It adds no input, action or execution authority.
            var holder = new XElement(Ns + "ContentControl", new XAttribute(Ui + "Key", prefix + "context"),
                new XAttribute("HorizontalContentAlignment", "Stretch"), new XAttribute("VerticalContentAlignment", "Stretch"), child);
            Visit(holder, new Scope(declaration, "item"), path + ".content", depth + 1);
            holder.SetAttributeValue(Ui + "With", Resolve(value, scope));
            holder.RemoveAnnotations<UiBindingScope>(); holder.AddAnnotation(new UiBindingScope("item", scope.BindingRoot, scope.BindingRoot));
            holder.AddAnnotation(new ExpandedTemplate());
            content?.Remove(); reference?.Remove(); foreach (var property in properties) property.Remove(); owner.Add(holder);
        }
        finally { if (resource != null) _resolving.Remove(resource); }
    }
}
