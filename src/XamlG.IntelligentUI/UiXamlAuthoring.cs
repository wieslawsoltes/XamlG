using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

/// <summary>Source-only authoring expansion. No runtime XAML loader or user assembly is involved.
/// Resources retain their declaration scope; every expanded visual still goes through UiCompiler.</summary>
internal sealed partial class UiXamlAuthoring(UiCatalog catalog, UiLimits limits, Action<XElement>? validate = null)
{
    private static readonly XNamespace Ns = UiCatalog.AvaloniaNamespace;
    private static readonly XNamespace Ui = UiCatalog.UiNamespace;
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly HashSet<Resource> _resolving = new(ReferenceEqualityComparer.Instance);
    private int _expanded;
    internal void Normalize(XElement root)
    {
        // Validate even declarations that are not referenced. Removing a declaration must not
        // silently grant CLR namespaces, executable directives, or an unbounded hidden subtree.
        var declarations = 0;
        foreach (var node in root.DescendantsAndSelf())
        {
            if (++declarations > limits.Nodes * 8) throw Error("node_limit", "Authoring declarations exceed their budget.");
            if (node.Name.Namespace != Ns && node.Name.Namespace != Ui && node.Name.Namespace != X)
                throw Error("unknown_namespace", "Unknown authoring namespace.");
            foreach (var attribute in node.Attributes())
            {
                if (attribute.IsNamespaceDeclaration && attribute.Value != Ns.NamespaceName && attribute.Value != Ui.NamespaceName && attribute.Value != X.NamespaceName)
                    throw Error("unknown_namespace", "Custom CLR namespaces require an application compilation host.");
                if (attribute.Name.Namespace == X && attribute.Name.LocalName is not ("Key" or "Name"))
                    throw Error("unknown_directive", "Executable/type directives require application compilation.");
            }
        }
        Visit(root, new Scope(null), "root", 0);
    }
    private void Visit(XElement element, Scope parent, string path, int depth)
    {
        if (element.Annotation<ExpandedTemplate>() != null) return;
        if (++_expanded > limits.Nodes * 8 || depth > limits.Depth) throw Error("node_limit", "Expanded authoring exceeds tree limits.");
        var scope = new Scope(parent, element.Attribute(Ui + "Each") != null ? "item" : null);
        var resourceNodes = Properties(element, "Resources");
        if (resourceNodes.Length > 1) throw Error("invalid_resource", "Duplicate Resources property.");
        if (resourceNodes.Length == 1) { ReadResources(resourceNodes[0], scope); resourceNodes[0].Remove(); }
        NormalizeContext(element, scope, parent.BindingRoot);
        var styleNodes = Properties(element, "Styles");
        if (styleNodes.Length > 1) throw Error("invalid_style", "Duplicate Styles property.");
        if (styleNodes.Length == 1)
        {
            if (element.Name.Namespace != Ns) throw Error("invalid_style", "Attach scoped styles to a native control.");
            var rules = ImmutableArray.CreateBuilder<XElement>();
            CheckAttributes(styleNodes[0]); CheckText(styleNodes[0]);
            foreach (var style in styleNodes[0].Elements()) ReadStyle(style, null, scope, rules);
            element.AddAnnotation(new UiStyleSources(rules.ToImmutable())); styleNodes[0].Remove();
        }
        var xname = (string?)element.Attribute(X + "Name");
        if (xname != null)
        {
            if (element.Attribute("Name") is { } named && named.Value != xname) throw Error("invalid_property", "Name conflicts with x:Name.");
            element.SetAttributeValue("Name", xname);
        }
        ExpandItemTemplate(element, scope, path, depth);
        ExpandContentTemplate(element, scope, path, depth);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.Elements().Where(IsProperty).ToArray())
        {
            var name = PropertyName(element, property);
            if (!seen.Add(name) || element.Attribute(name) != null) throw Error("invalid_property", "Duplicate property: " + name);
            CheckAttributes(property);
            var values = property.Elements().ToArray();
            if (IsContentProperty(element, name) && values.Length != 0 && values.All(IsVisual))
            {
                if (element.Elements().Any(child => !IsProperty(child))) throw Error("invalid_content", "Explicit content conflicts with implicit children.");
                CheckText(property); property.ReplaceWith(values); continue;
            }
            var value = name is "RowDefinitions" or "ColumnDefinitions" && values.Length != 0
                ? Definitions(name, property) : ScalarProperty(property, scope);
            element.SetAttributeValue(name, value); property.Remove();
        }
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName.Length == 0).ToArray())
            attribute.Value = Resolve(attribute.Value, scope);
        var index = 0;
        foreach (var child in element.Elements().ToArray()) Visit(child, scope, path + "." + index++, depth + 1);
    }
    private XElement[] Properties(XElement owner, string name) => owner.Elements().Where(child => IsProperty(child) && PropertyName(owner, child) == name).ToArray();
    private static bool IsProperty(XElement element) => element.Name.LocalName.IndexOf('.') >= 0;
    private static string PropertyName(XElement owner, XElement property)
    {
        var prefix = owner.Name.LocalName + ".";
        if (property.Name.Namespace != owner.Name.Namespace || !property.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal))
            throw Error("unknown_property", "Property elements must name their containing control.");
        return property.Name.LocalName[prefix.Length..];
    }
    private bool IsVisual(XElement node) => node.Name.Namespace == Ns && catalog.Components.ContainsKey(node.Name.LocalName) ||
        node.Name.Namespace == Ui && UiCompositeCatalog.Components.ContainsKey(node.Name.LocalName);
    private bool IsContentProperty(XElement owner, string name)
    {
        if (!catalog.Components.TryGetValue(owner.Name.LocalName, out var component)) return false;
        if (name == "Content") return component.Properties.ContainsKey("Content") || owner.Name.LocalName == "ScrollViewer";
        if (name == "Child") return owner.Name.LocalName is "Border" or "Viewbox" or "LayoutTransformControl";
        if (name == "Children") return owner.Name.LocalName is "StackPanel" or "Grid" or "Panel" or "Canvas" or "WrapPanel" or "DockPanel" or "UniformGrid";
        return name == "Items" && owner.Name.LocalName is "ItemsControl" or "ListBox" or "ComboBox" or "TreeView" or "TreeViewItem" or "TabControl";
    }
    private static string Definitions(string name, XElement property)
    {
        CheckText(property); var tag = name == "RowDefinitions" ? "RowDefinition" : "ColumnDefinition";
        var member = tag == "RowDefinition" ? "Height" : "Width"; var result = new List<string>();
        foreach (var child in property.Elements())
        {
            if (child.Name != Ns + tag || child.HasElements) throw Error("invalid_property", "Invalid grid definition element.");
            CheckAttributes(child, member); CheckText(child); result.Add((string?)child.Attribute(member) ?? "*");
            if (result.Count > 64) throw Error("invalid_property", "Grid track budget exceeded.");
        }
        return string.Join(",", result);
    }
    private string ScalarProperty(XElement property, Scope scope)
    {
        var children = property.Elements().ToArray();
        if (children.Length == 0) return Resolve(property.Value, scope);
        CheckText(property);
        if (children.Length != 1) throw Error("invalid_property", "A scalar property requires exactly one value.");
        return Scalar(children[0], scope);
    }
    private string Scalar(XElement node, Scope scope)
    {
        if (node.Name == Ns + "Binding" || node.Name == Ns + "CompiledBinding") return BindingMarkup(node);
        if (node.Name == X + "Null") { CheckAttributes(node); CheckText(node); if (node.HasElements) throw Error("invalid_resource", "Invalid null value."); return "{ui:Expr null}"; }
        if (node.Name == X + "Array")
        {
            CheckAttributes(node, "Type"); CheckText(node);
            if (node.Attribute("Type") is { } type && type.Value is not ("x:String" or "{x:Type x:String}")) throw Error("invalid_resource", "Only string arrays are supported by native ItemsSource.");
            var values = node.Elements().ToArray();
            if (values.Length > 512 || values.Any(child => child.Name != X + "String")) throw Error("invalid_resource", "Expected a bounded string array.");
            return JsonSerializer.Serialize(values.Select(child => Scalar(child, scope)).ToArray());
        }
        if (node.Name == Ns + "StaticResource" || node.Name == Ns + "DynamicResource")
        {
            CheckAttributes(node, "ResourceKey"); CheckText(node);
            if (node.HasElements) throw Error("invalid_resource", "Invalid resource reference.");
            return ResourceValue(Find(scope, Required(node, "ResourceKey")));
        }
        if (node.Name == Ns + "SolidColorBrush")
        {
            CheckAttributes(node, "Color"); CheckText(node);
            if (node.HasElements) throw Error("invalid_resource", "Brush values must use declared attributes.");
            return Resolve((string?)node.Attribute("Color") ?? "Transparent", scope);
        }
        if (node.Name == Ns + "MatrixTransform")
        {
            CheckAttributes(node, "Matrix"); CheckText(node);
            if (node.HasElements) throw Error("invalid_resource", "Invalid matrix transform.");
            var matrix = Resolve(Required(node, "Matrix"), scope);
            return matrix.StartsWith("matrix(", StringComparison.Ordinal) ? matrix : "matrix(" + matrix + ")";
        }
        if (node.Name == Ns + "StreamGeometry") { CheckAttributes(node); if (node.HasElements) throw Error("invalid_resource", "Invalid geometry."); return Resolve(node.Value, scope); }
        if (node.Name.Namespace == X && node.Name.LocalName is "String" or "Double" or "Decimal" or "Int32" or "Boolean" ||
            node.Name.Namespace == Ns && node.Name.LocalName is "Color" or "Thickness" or "CornerRadius" or "Point")
        {
            CheckAttributes(node); if (node.HasElements) throw Error("invalid_resource", "Scalar values cannot contain elements.");
            return Resolve(node.Value, scope);
        }
        throw Error("invalid_resource", "Unsupported resource value: " + node.Name.LocalName);
    }
    private string Resolve(string value, Scope scope)
    {
        var prefix = value.StartsWith("{StaticResource ", StringComparison.Ordinal) ? "{StaticResource " :
            value.StartsWith("{DynamicResource ", StringComparison.Ordinal) ? "{DynamicResource " : null;
        if (prefix == null) return value;
        if (!value.EndsWith('}')) throw Error("invalid_resource", "Incomplete resource reference.");
        var key = value[prefix.Length..^1].Trim(); UiJson.Identifier(key, "Resource key");
        return ResourceValue(Find(scope, key));
    }
    private string ResourceValue(Resource resource)
    {
        if (_resolving.Count >= limits.Depth || !_resolving.Add(resource)) throw Error("resource_cycle", "Resource reference cycle or depth limit.");
        try { return Scalar(resource.Node, resource.Scope); }
        finally { _resolving.Remove(resource); }
    }
    private static Resource Find(Scope scope, string key)
    {
        for (Scope? current = scope; current != null; current = current.Parent)
            if (current.Values.TryGetValue(key, out var resource)) return resource;
        throw Error("unknown_resource", "Resource was not found: " + key);
    }
    private void ReadResources(XElement wrapper, Scope scope)
    {
        CheckAttributes(wrapper); CheckText(wrapper);
        var nodes = wrapper.Elements().ToArray();
        if (nodes.Length == 1 && nodes[0].Name == Ns + "ResourceDictionary") { CheckAttributes(nodes[0]); CheckText(nodes[0]); nodes = nodes[0].Elements().ToArray(); }
        var merged = nodes.Where(node => node.Name == Ns + "ResourceDictionary.MergedDictionaries").ToArray();
        if (merged.Length > 1) throw Error("invalid_resource", "Duplicate merged dictionary collection.");
        if (merged.Length == 1)
        {
            CheckAttributes(merged[0]); CheckText(merged[0]);
            foreach (var dictionary in merged[0].Elements())
            {
                if (dictionary.Name != Ns + "ResourceDictionary") throw Error("invalid_resource", "Only inline resource dictionaries are allowed; no resource fetching.");
                var child = new Scope(scope); ReadResources(dictionary, child);
                foreach (var value in child.Values) scope.Values[value.Key] = value.Value;
            }
        }
        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Except(merged))
        {
            var key = (string?)node.Attribute(X + "Key") ?? throw Error("invalid_resource", "Resource requires x:Key.");
            UiJson.Identifier(key, "Resource key");
            if (!local.Add(key)) throw Error("duplicate_resource", "Duplicate resource key: " + key);
            var clone = new XElement(node); clone.Attribute(X + "Key")!.Remove();
            scope.Values[key] = new Resource(clone, scope);
        }
        // Validate unused values as well. Templates are source, never constructed objects.
        foreach (var key in local)
        {
            var resource = scope.Values[key];
            if (resource.Node.Name == Ns + "DataTemplate") ValidateUnusedTemplate(resource);
            else ResourceValue(resource);
        }
    }
    private static void CheckAttributes(XElement element, params string[] allowed)
    {
        if (element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && (attribute.Name.NamespaceName.Length != 0 || !allowed.Contains(attribute.Name.LocalName, StringComparer.Ordinal))))
            throw Error("invalid_authoring", "Unsupported attribute on " + element.Name.LocalName + ".");
    }
    private static void CheckText(XElement element)
    {
        if (element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value))) throw Error("invalid_authoring", "Unexpected text in " + element.Name.LocalName + ".");
    }
    private static string Required(XElement element, string name) => (string?)element.Attribute(name) ?? throw Error("invalid_authoring", "Missing " + name + ".");
    private static UiException Error(string code, string message) => new(code, message);
    private sealed class Scope(Scope? parent, string? bindingRoot = null)
    {
        internal Scope? Parent { get; } = parent;
        internal string BindingRoot { get; set; } = bindingRoot ?? parent?.BindingRoot ?? "data";
        internal Dictionary<string, Resource> Values { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Resource(XElement node, Scope scope)
    {
        internal XElement Node { get; } = node;
        internal Scope Scope { get; } = scope;
    }
}
