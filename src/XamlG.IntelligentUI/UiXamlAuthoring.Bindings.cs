using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed partial class UiXamlAuthoring
{
    private void NormalizeContext(XElement element, Scope scope, string repeatRoot)
    {
        var properties = Properties(element, "DataContext");
        var attribute = element.Attribute("DataContext");
        if (properties.Length > 1 || properties.Length != 0 && attribute != null)
            throw Error("invalid_binding", "Duplicate DataContext.");
        string? value = attribute?.Value;
        if (properties.Length == 1)
        {
            CheckAttributes(properties[0]); value = ScalarProperty(properties[0], scope); properties[0].Remove();
        }
        if (value != null)
        {
            if (element.Attribute(Ui + "With") != null) throw Error("invalid_binding", "DataContext conflicts with ui:With.");
            element.SetAttributeValue(Ui + "With", Resolve(value, scope)); attribute?.Remove();
        }
        var contextRoot = scope.BindingRoot;
        if (element.Attribute(Ui + "With") != null) scope.BindingRoot = "item";
        element.AddAnnotation(new UiBindingScope(scope.BindingRoot, contextRoot, repeatRoot));
    }
    private static string BindingMarkup(XElement element)
    {
        CheckAttributes(element, "Path", "Mode", "FallbackValue", "TargetNullValue", "StringFormat"); CheckText(element);
        if (element.HasElements) throw Error("invalid_binding", "Binding object values must be inert path/options, not CLR objects.");
        var options = new List<string>();
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            var quote = attribute.Value.Contains('\'') ? '"' : '\'';
            if (attribute.Value.Contains(quote)) throw Error("invalid_binding", "Use attribute binding syntax for values containing both quote kinds.");
            options.Add(attribute.Name.LocalName + "=" + quote + attribute.Value + quote);
        }
        return "{" + element.Name.LocalName + (options.Count == 0 ? "" : " " + string.Join(",", options)) + "}";
    }
    private void ValidateUnusedTemplate(Resource resource)
    {
        ValidateTemplate(resource.Node);
        if (_resolving.Count >= limits.Depth || !_resolving.Add(resource)) throw Error("resource_cycle", "Template resource cycle.");
        try
        {
            var clone = new XElement(resource.Node.Elements().Single());
            Visit(clone, new Scope(resource.Scope, "item"), "validation", 0);
            validate?.Invoke(clone);
        }
        finally { _resolving.Remove(resource); }
    }
}
