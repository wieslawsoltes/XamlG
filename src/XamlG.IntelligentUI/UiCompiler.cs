using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

public sealed record UiCompilation(UiTemplate? Template, ImmutableArray<UiDiagnostic> Diagnostics, bool IsComplete)
{
    public bool Success => Template != null;
}
internal sealed record UiValue(JsonElement Literal, IUiExpression? Expression = null)
{
    internal JsonElement Resolve(JsonElement state, JsonElement data, JsonElement? item) => Expression?.Evaluate(state, data, item) ?? Literal;
}
internal sealed record UiPlanNode(string Key, UiComponent Component, ImmutableDictionary<string, UiValue> Properties,
    ImmutableArray<UiPlanNode> Children, string? StateKey, string? ActionId, IUiExpression? When, IUiExpression? Each, IUiExpression? ItemKey);

/// <summary>Compiles a declared Avalonia XAML vocabulary to immutable operations. The default
/// expression backend is non-executable. Only a trusted host may supply another expression compiler.</summary>
public sealed class UiCompiler(UiCatalog? catalog = null, UiLimits? limits = null, IUiExpressionCompiler? expressionCompiler = null)
{
    public UiCatalog Catalog { get; } = catalog ?? UiCatalog.Default;
    public UiLimits Limits { get; } = limits ?? new();
    public string ExpressionLanguage => expressionCompiler?.Language ?? "csharp-pure";
    public IUiExpression CompileExpression(string source) => expressionCompiler?.Compile(source, Limits) ?? UiExpression.Parse(source, Limits);
    public UiCompilation Compile(string xaml, bool isFinal = true)
    {
        ArgumentNullException.ThrowIfNull(xaml); Limits.Validate();
        if (xaml.Length > Limits.SourceCharacters) return new(null, [new("source_limit", "XAML exceeds the source limit.")], isFinal);
        var diagnostics = ImmutableArray.CreateBuilder<UiDiagnostic>();
        try
        {
            var document = Read(xaml, isFinal, diagnostics);
            if (document?.Root == null) return new(null, diagnostics.ToImmutable(), false);
            var keys = new HashSet<string>(StringComparer.Ordinal); var count = 0;
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
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Limits.SourceCharacters, IgnoreComments = true, IgnoreProcessingInstructions = false };
        using var reader = XmlReader.Create(new StringReader(source), settings);
        var document = new XDocument(); var stack = new Stack<XElement>();
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
                    case XmlNodeType.Text: case XmlNodeType.SignificantWhitespace: case XmlNodeType.CDATA:
                        if (stack.Count > 0) stack.Peek().Add(new XText(reader.Value));
                        break;
                    case XmlNodeType.ProcessingInstruction: throw new UiException("invalid_xaml", "Processing instructions are not permitted.");
                }
            }
        }
        catch (XmlException error) when (!final && document.Root != null)
        { diagnostics.Add(new("partial_xaml", error.Message, Line: error.LineNumber, Column: error.LinePosition)); }
        return document;
    }
    private UiPlanNode Parse(XElement element, string implicitKey, int depth, HashSet<string> keys, ref int count)
    {
        if (++count > Limits.Nodes || depth > Limits.Depth) throw new UiException("node_limit", "XAML exceeds tree limits.");
        if (element.Name.NamespaceName != UiCatalog.AvaloniaNamespace) throw new UiException("unknown_namespace", "Only the declared Avalonia component namespace is allowed.");
        var component = Catalog.Get(element.Name.LocalName);
        XNamespace ui = UiCatalog.UiNamespace; XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var key = (string?)element.Attribute(ui + "Key") ?? (string?)element.Attribute(x + "Name") ?? implicitKey;
        UiJson.Identifier(key, "UI key");
        if (!keys.Add(key)) throw new UiException("duplicate_key", "Duplicate UI key: " + key);
        string? stateKey = null, actionId = null;
        IUiExpression? when = null, each = null, itemKey = null;
        var properties = ImmutableDictionary.CreateBuilder<string, UiValue>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                if (attribute.Value is not (UiCatalog.AvaloniaNamespace or UiCatalog.UiNamespace or "http://schemas.microsoft.com/winfx/2006/xaml")) throw new UiException("unknown_namespace", "Custom CLR and XAML namespaces require an application compilation host.");
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
            if (attribute.Name.NamespaceName.Length != 0 || !component.Properties.TryGetValue(attribute.Name.LocalName, out var property)) throw new UiException("unknown_property", "Property is not in the catalog: " + attribute.Name);
            properties.Add(attribute.Name.LocalName, Value(attribute.Value, property));
        }
        if (stateKey != null && component.InputProperty == null) throw new UiException("invalid_binding", "This component has no input property.");
        if (stateKey != null && properties.ContainsKey(component.InputProperty!)) throw new UiException("invalid_binding", "Do not combine ui:Bind with a value for the input property.");
        if (actionId != null && !component.SupportsAction) throw new UiException("invalid_action", "This component does not expose an action.");
        if (each != null && itemKey == null || each == null && itemKey != null) throw new UiException("invalid_repeat", "ui:Each and ui:ItemKey must be supplied together.");
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (component.TextProperty == null || properties.ContainsKey(component.TextProperty)) throw new UiException("invalid_content", "Unexpected or duplicate text content.");
            properties.Add(component.TextProperty, Value(text, component.Properties[component.TextProperty]));
        }
        var children = ImmutableArray.CreateBuilder<UiPlanNode>();
        foreach (var child in element.Elements()) children.Add(Parse(child, key + "." + children.Count, depth + 1, keys, ref count));
        if (children.Count > component.MaximumChildren || component.ChildTypes is { } allowed && children.Any(child => !allowed.Contains(child.Component.Name, StringComparer.Ordinal))) throw new UiException("invalid_content", "Invalid children for " + component.Name);
        if (children.Count != 0 && (properties.ContainsKey("ItemsSource") || properties.ContainsKey("Content"))) throw new UiException("invalid_content", "Do not combine child elements with scalar Content or ItemsSource.");
        return new(key, component, properties.ToImmutable(), children.ToImmutable(), stateKey, actionId, when, each, itemKey);
    }
    private UiValue Value(string text, UiProperty property)
    {
        if (text.Length > Limits.TextCharacters) throw new UiException("text_limit", "Property text exceeds the configured limit.");
        if (text.StartsWith("{}", StringComparison.Ordinal)) return new(property.ReadLiteral(text[2..]));
        if (text.StartsWith("{", StringComparison.Ordinal)) return new(default, Expression(text));
        return new(property.ReadLiteral(text));
    }
    private IUiExpression Expression(string source)
    {
        const string prefix = "{ui:Expr ";
        if (!source.StartsWith(prefix, StringComparison.Ordinal) || !source.EndsWith('}')) throw new UiException("invalid_expression", "Use {ui:Expr <C# expression>}; arbitrary markup extensions require application compilation.");
        return CompileExpression(source[prefix.Length..^1]);
    }
}

public sealed class UiTemplate
{
    private readonly UiPlanNode _root;
    private readonly UiLimits _limits;
    internal UiTemplate(UiPlanNode root, UiLimits limits) { _root = root; _limits = limits; }
    public ImmutableArray<UiElement> Render(JsonElement state, JsonElement data)
    {
        var count = 0; var keys = new HashSet<string>(StringComparer.Ordinal);
        return RenderNode(_root, state, data, null, "", keys, ref count);
    }
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
        var element = new UiElement(nodeKey, node.Component.Name, properties, children.ToImmutable(), node.StateKey, node.ActionId);
        UiTreeValidation.ValidateElement(element, node.Component, _limits.TextCharacters);
        return [element];
    }
}

/// <summary>Cross-property checks shared by interpreted and transport-provided resolved trees.</summary>
public static class UiTreeValidation
{
    public static void ValidateElement(UiElement node, UiComponent component, int textLimit = 16384)
    {
        if (node.Children.IsDefault || node.Properties == null || node.Children.Length > component.MaximumChildren ||
            component.ChildTypes is { } allowed && node.Children.Any(child => !allowed.Contains(child.Type, StringComparer.Ordinal))) throw new UiException("invalid_content", "Invalid child container.");
        if (node.ActionId != null && !component.SupportsAction || node.StateKey != null && component.InputProperty == null) throw new UiException("invalid_tree", "Undeclared input or action capability.");
        var properties = node.Properties;
        foreach (var property in properties)
        {
            if (!component.Properties.TryGetValue(property.Key, out var definition)) throw new UiException("unknown_property", "Undeclared property.");
            definition.ValidateValue(property.Value);
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.Length > textLimit) throw new UiException("text_limit", "Computed property text exceeds the limit.");
        }
        if (node.Children.Length != 0 && (properties.ContainsKey("ItemsSource") || properties.ContainsKey("Content"))) throw new UiException("invalid_content", "Child elements conflict with Content or ItemsSource.");
        if (node.Type is "Slider" or "ProgressBar" or "NumericUpDown")
        {
            var min = properties.TryGetValue("Minimum", out var lower) ? lower.GetDecimal() : node.Type == "NumericUpDown" ? -1000000 : 0;
            var max = properties.TryGetValue("Maximum", out var upper) ? upper.GetDecimal() : node.Type == "NumericUpDown" ? 1000000 : 100;
            var value = properties.TryGetValue("Value", out var current) && current.ValueKind != JsonValueKind.Null ? current.GetDecimal() : (decimal?)null;
            if (min >= max || value is { } number && (number < min || number > max)) throw new UiException("invalid_range", "Range requires Minimum < Maximum and Value within that range.");
        }
        if (properties.TryGetValue("SelectedIndex", out var selected))
        {
            var count = properties.TryGetValue("ItemsSource", out var items) ? items.GetArrayLength() : node.Children.Length;
            if (selected.GetDecimal() >= count) throw new UiException("invalid_selection", "SelectedIndex is outside the current items.");
        }
        foreach (var property in properties.Where(p => p.Key is "ColumnDefinitions" or "RowDefinitions")) ValidateDefinitions(property.Value.GetString()!);
        if (properties.TryGetValue("FormatString", out var format)) ValidateFormat(format.GetString()!);
    }
    private static void ValidateFormat(string format)
    {
        if (format.Length > 64 || format.Length > 1 && char.IsAsciiLetter(format[0]) && format.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 &&
            (!int.TryParse(format.AsSpan(1), out var precision) || precision > 16)) throw new UiException("invalid_format", "Numeric format strings are limited to 64 characters and precision 16.");
    }
    private static void ValidateDefinitions(string source)
    {
        var parts = source.Split(',');
        if (parts.Length > 64) throw new UiException("invalid_property", "At most 64 grid definitions are allowed.");
        foreach (var raw in parts)
        {
            var value = raw.Trim(); if (value is "Auto" or "*") continue;
            if (value.EndsWith('*')) value = value[..^1];
            if (!decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0 || number > 10000) throw new UiException("invalid_property", "Invalid grid definition.");
        }
    }
}
