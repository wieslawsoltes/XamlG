using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

public sealed record UiCompilation(UiTemplate? Template, ImmutableArray<UiDiagnostic> Diagnostics, bool IsComplete)
{
    public bool Success => Template != null;
}
internal sealed record UiValue(JsonElement Literal, UiExpression? Expression = null)
{
    internal JsonElement Resolve(JsonElement state, JsonElement data, JsonElement? item) => Expression?.Evaluate(state, data, item) ?? Literal;
}
internal sealed record UiPlanNode(string Key, UiComponent Component, ImmutableDictionary<string, UiValue> Properties,
    ImmutableArray<UiPlanNode> Children, string? StateKey, string? ActionId, UiExpression? When, UiExpression? Each, UiExpression? ItemKey);

/// <summary>Compiles a restricted Avalonia XAML vocabulary to immutable, non-executable operations.</summary>
public sealed class UiCompiler(UiCatalog? catalog = null, UiLimits? limits = null)
{
    public UiCatalog Catalog { get; } = catalog ?? UiCatalog.Default;
    public UiLimits Limits { get; } = limits ?? new();
    public UiCompilation Compile(string xaml, bool isFinal = true)
    {
        Limits.Validate();
        if (xaml.Length > Limits.SourceCharacters) return new(null, [new("source_limit", "XAML exceeds the source limit.")], isFinal);
        var diagnostics = ImmutableArray.CreateBuilder<UiDiagnostic>();
        try
        {
            var document = Read(xaml, isFinal, diagnostics);
            if (document?.Root == null) return new(null, diagnostics.ToImmutable(), false);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            var root = Parse(document.Root, "root", 0, keys, ref count);
            return new(new UiTemplate(root, Limits), diagnostics.ToImmutable(), diagnostics.Count == 0);
        }
        catch (Exception error) when (error is UiException or XmlException)
        {
            diagnostics.Add(error is XmlException xml ? new("invalid_xaml", xml.Message, Line: xml.LineNumber, Column: xml.LinePosition) : new(((UiException)error).Code, error.Message));
            return new(null, diagnostics.ToImmutable(), false);
        }
    }
    private XDocument? Read(string source, bool final, ImmutableArray<UiDiagnostic>.Builder diagnostics)
    {
        // XmlReader never resolves external entities or a DTD. The streaming builder only
        // publishes start tags after the XML parser has validated all their attributes.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Limits.SourceCharacters, IgnoreComments = true, IgnoreProcessingInstructions = false };
        using var reader = XmlReader.Create(new StringReader(source), settings);
        var document = new XDocument();
        var stack = new Stack<XElement>();
        try
        {
            while (reader.Read())
            {
                if (reader.Depth > Limits.Depth) throw new UiException("depth_limit", "XAML exceeds the nesting limit.");
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                if (reader.Name == "xmlns") element.Add(new XAttribute("xmlns", reader.Value));
                                else element.Add(new XAttribute(XName.Get(reader.LocalName, reader.NamespaceURI), reader.Value));
                            } while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }
                        if (stack.Count == 0) document.Add(element); else stack.Peek().Add(element);
                        if (!reader.IsEmptyElement) stack.Push(element);
                        break;
                    case XmlNodeType.EndElement: stack.Pop(); break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.CDATA:
                        if (stack.Count > 0) stack.Peek().Add(new XText(reader.Value));
                        break;
                    case XmlNodeType.ProcessingInstruction: throw new UiException("invalid_xaml", "Processing instructions are not permitted.");
                }
            }
        }
        catch (XmlException error) when (!final && document.Root != null)
        {
            // A complete prefix remains usable while the source grows. It is never final;
            // once final=true the exact same malformed source is rejected transactionally.
            diagnostics.Add(new("partial_xaml", error.Message, Line: error.LineNumber, Column: error.LinePosition));
        }
        return document;
    }
    private UiPlanNode Parse(XElement element, string implicitKey, int depth, HashSet<string> keys, ref int count)
    {
        if (++count > Limits.Nodes || depth > Limits.Depth) throw new UiException("node_limit", "XAML exceeds tree limits.");
        if (element.Name.NamespaceName != UiCatalog.AvaloniaNamespace) throw new UiException("unknown_namespace", "Only the Avalonia component namespace is allowed.");
        var component = Catalog.Get(element.Name.LocalName);
        XNamespace ui = UiCatalog.UiNamespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var key = (string?)element.Attribute(ui + "Key") ?? (string?)element.Attribute(x + "Name") ?? implicitKey;
        UiJson.Identifier(key, "UI key");
        if (!keys.Add(key)) throw new UiException("duplicate_key", "Duplicate UI key: " + key);
        string? stateKey = null, actionId = null;
        UiExpression? when = null, each = null, itemKey = null;
        var properties = ImmutableDictionary.CreateBuilder<string, UiValue>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                if (attribute.Value is not (UiCatalog.AvaloniaNamespace or UiCatalog.UiNamespace or "http://schemas.microsoft.com/winfx/2006/xaml"))
                    throw new UiException("unknown_namespace", "Custom CLR and XAML namespaces are not permitted.");
                continue;
            }
            if (attribute.Name == x + "Name" || attribute.Name == ui + "Key") continue;
            if (attribute.Name.NamespaceName == UiCatalog.UiNamespace)
            {
                switch (attribute.Name.LocalName)
                {
                    case "Bind": stateKey = attribute.Value; UiJson.Identifier(stateKey, "State key"); break;
                    case "Action": actionId = attribute.Value; UiJson.Identifier(actionId, "Action ID"); break;
                    case "When": when = Expression(attribute.Value); break;
                    case "Each": each = Expression(attribute.Value); break;
                    case "ItemKey": itemKey = Expression(attribute.Value); break;
                    default: throw new UiException("unknown_directive", "Unknown intelligent UI directive.");
                }
                continue;
            }
            if (attribute.Name.NamespaceName.Length != 0 || !component.Properties.TryGetValue(attribute.Name.LocalName, out var property))
                throw new UiException("unknown_property", "Property is not in the catalog: " + attribute.Name);
            properties.Add(attribute.Name.LocalName, Value(attribute.Value, property));
        }
        if (stateKey != null && component.InputProperty == null) throw new UiException("invalid_binding", "This component has no input property.");
        if (stateKey != null && properties.ContainsKey(component.InputProperty!)) throw new UiException("invalid_binding", "Do not combine ui:Bind with a value for the input property.");
        if (actionId != null && component.Name != "Button") throw new UiException("invalid_action", "Actions must be attached to a Button.");
        if (each != null && itemKey == null || each == null && itemKey != null) throw new UiException("invalid_repeat", "ui:Each and ui:ItemKey must be supplied together.");
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (component.TextProperty == null || properties.ContainsKey(component.TextProperty)) throw new UiException("invalid_content", "Unexpected or duplicate text content.");
            properties.Add(component.TextProperty, Value(text, component.Properties[component.TextProperty]));
        }
        var children = ImmutableArray.CreateBuilder<UiPlanNode>();
        foreach (var child in element.Elements()) children.Add(Parse(child, key + "." + children.Count, depth + 1, keys, ref count));
        if (children.Count > component.MaximumChildren) throw new UiException("invalid_content", "Too many children for " + component.Name);
        return new(key, component, properties.ToImmutable(), children.ToImmutable(), stateKey, actionId, when, each, itemKey);
    }
    private UiValue Value(string text, UiProperty property)
    {
        if (text.StartsWith("{}", StringComparison.Ordinal)) return new(property.ReadLiteral(text[2..]));
        if (text.StartsWith("{", StringComparison.Ordinal)) return new(default, Expression(text));
        return new(property.ReadLiteral(text));
    }
    private UiExpression Expression(string source)
    {
        const string prefix = "{ui:Expr ";
        if (!source.StartsWith(prefix, StringComparison.Ordinal) || !source.EndsWith('}'))
            throw new UiException("invalid_expression", "Use {ui:Expr <bounded C# expression>}; arbitrary markup extensions are not permitted.");
        return UiExpression.Parse(source[prefix.Length..^1], Limits);
    }
}

public sealed class UiTemplate
{
    private readonly UiPlanNode _root;
    private readonly UiLimits _limits;
    internal UiTemplate(UiPlanNode root, UiLimits limits) { _root = root; _limits = limits; }
    public ImmutableArray<UiElement> Render(JsonElement state, JsonElement data)
    {
        var count = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        return RenderNode(_root, state, data, null, "", keys, ref count);
    }
    private ImmutableArray<UiElement> RenderNode(UiPlanNode node, JsonElement state, JsonElement data, JsonElement? item,
        string scope, HashSet<string> keys, ref int count, bool expanded = false)
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
        if (node.Component.Name is "Slider" or "ProgressBar")
        {
            var min = properties.TryGetValue("Minimum", out var lower) ? lower.GetDecimal() : 0;
            var max = properties.TryGetValue("Maximum", out var upper) ? upper.GetDecimal() : 100;
            var value = properties.TryGetValue("Value", out var current) ? current.GetDecimal() : 0;
            if (min >= max || value < min || value > max) throw new UiException("invalid_range", "Range requires Minimum < Maximum and a Value within that range.");
        }
        foreach (var property in properties.Where(p => p.Key.StartsWith("Grid.", StringComparison.Ordinal)))
            if (property.Value.GetDecimal() != decimal.Truncate(property.Value.GetDecimal())) throw new UiException("invalid_property", "Grid indices and spans must be integers.");
        foreach (var property in properties.Where(p => p.Key is "ColumnDefinitions" or "RowDefinitions"))
            ValidateDefinitions(property.Value.GetString()!);
        var children = ImmutableArray.CreateBuilder<UiElement>();
        foreach (var child in node.Children) children.AddRange(RenderNode(child, state, data, item, scope, keys, ref count));
        if (children.Count > node.Component.MaximumChildren) throw new UiException("invalid_content", "Expanded children exceed container capacity.");
        return [new(nodeKey, node.Component.Name, properties, children.ToImmutable(), node.StateKey, node.ActionId)];
    }
    private static void ValidateDefinitions(string source)
    {
        var parts = source.Split(',');
        if (parts.Length > 64) throw new UiException("invalid_property", "At most 64 grid definitions are allowed.");
        foreach (var raw in parts)
        {
            var value = raw.Trim();
            if (value is "Auto" or "*") continue;
            if (value.EndsWith('*')) value = value[..^1];
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0 || number > 10000)
                throw new UiException("invalid_property", "Invalid grid definition.");
        }
    }
}
