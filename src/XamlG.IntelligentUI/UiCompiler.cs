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
    ImmutableArray<UiPlanNode> Children, string? StateKey, string? ActionId, IUiExpression? When, IUiExpression? Each, IUiExpression? ItemKey)
{
    internal ImmutableArray<UiPlanStyle> Styles { get; init; } = [];
    internal UiPlanControlTemplate? ControlTemplate { get; init; }
    internal UiPlanControlTheme? ControlTheme { get; init; }
    internal UiValue? Context { get; init; }
}

/// <summary>Compiles a declared Avalonia XAML vocabulary to immutable operations. The default
/// expression backend is non-executable. Only a trusted host may supply another expression compiler.</summary>
public sealed partial class UiCompiler(UiCatalog? catalog = null, UiLimits? limits = null, IUiExpressionCompiler? expressionCompiler = null)
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
            new UiXamlAuthoring(Catalog, Limits, ValidateAuthoringTemplate).Normalize(document.Root);
            var keys = new HashSet<string>(StringComparer.Ordinal); var count = 0;
            var root = Parse(document.Root, "root", 0, keys, ref count);
            return new(new UiTemplate(root, Limits, Catalog), diagnostics.ToImmutable(), diagnostics.Count == 0);
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
        UiComponent component;
        if (element.Name.NamespaceName == UiCatalog.AvaloniaNamespace) component = Catalog.Get(element.Name.LocalName);
        else if (element.Name.NamespaceName == UiCatalog.UiNamespace)
            component = UiCompositeCatalog.Components.TryGetValue(element.Name.LocalName, out var composite) ? composite
                : throw new UiException("unknown_component", "Unknown response component: " + element.Name.LocalName);
        else throw new UiException("unknown_namespace", "Only declared Avalonia and intelligent UI component namespaces are allowed.");
        XNamespace ui = UiCatalog.UiNamespace; XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var key = (string?)element.Attribute(ui + "Key") ?? (string?)element.Attribute(x + "Name") ?? (string?)element.Attribute("Name") ?? implicitKey;
        UiJson.Identifier(key, "UI key");
        if (!keys.Add(key)) throw new UiException("duplicate_key", "Duplicate UI key: " + key);
        string? stateKey = null, actionId = null;
        UiValue? context = null;
        var bindingScope = element.Annotation<UiBindingScope>() ?? new("data", "data", "data");
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
                    case "Bind": if (stateKey != null) throw new UiException("invalid_binding", "Duplicate state input binding."); stateKey = attribute.Value; UiJson.Identifier(stateKey, "State key"); break;
                    case "Action": actionId = attribute.Value; UiJson.Identifier(actionId, "Action ID"); break;
                    case "With": context = ContextValue(attribute.Value, bindingScope.ContextRoot); break;
                    case "When": when = ScopedExpression(attribute.Value, bindingScope.Root); break;
                    case "Each": each = RepeatExpression(attribute.Value, bindingScope.RepeatRoot); break;
                    case "ItemKey": itemKey = ScopedExpression(attribute.Value, "item"); break;
                    default: throw new UiException("unknown_directive", "Unknown intelligent UI directive.");
                }
                continue;
            }
            if (attribute.Name.NamespaceName.Length != 0 || !component.Properties.TryGetValue(attribute.Name.LocalName, out var property)) throw new UiException("unknown_property", "Property is not in the catalog: " + attribute.Name);
            if (UiBindingExpression.IsBinding(attribute.Value))
            {
                var binding = UiBindingExpression.Parse(attribute.Value, bindingScope.Root, Limits);
                if (binding.Mode == "TwoWay" || binding.Mode == "Default" && component.InputProperty == attribute.Name.LocalName && binding.Root == "state")
                {
                    if (stateKey != null || component.InputProperty != attribute.Name.LocalName || binding.Root != "state" || binding.Path.Length != 1 ||
                        binding.Fallback != null || binding.TargetNull != null || binding.Format != null)
                        throw new UiException("invalid_binding", "TwoWay requires one declared state slot and no value conversion.");
                    stateKey = binding.Path[0]; UiJson.Identifier(stateKey, "State key"); continue;
                }
            }
            properties.Add(attribute.Name.LocalName, Value(attribute.Value, property, bindingScope.Root));
        }
        if (stateKey != null && component.InputProperty == null) throw new UiException("invalid_binding", "This component has no input property.");
        if (stateKey != null && properties.ContainsKey(component.InputProperty!)) throw new UiException("invalid_binding", "Do not combine ui:Bind with a value for the input property.");
        if (actionId != null && !component.SupportsAction) throw new UiException("invalid_action", "This component does not expose an action.");
        if (each != null && itemKey == null || each == null && itemKey != null) throw new UiException("invalid_repeat", "ui:Each and ui:ItemKey must be supplied together.");
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (component.TextProperty == null || properties.ContainsKey(component.TextProperty)) throw new UiException("invalid_content", "Unexpected or duplicate text content.");
            properties.Add(component.TextProperty, Value(text, component.Properties[component.TextProperty], bindingScope.Root));
        }
        var children = ImmutableArray.CreateBuilder<UiPlanNode>();
        foreach (var child in element.Elements()) children.Add(Parse(child, key + "." + children.Count, depth + 1, keys, ref count));
        if (children.Count > component.MaximumChildren || component.ChildTypes is { } allowed && children.Any(child => !allowed.Contains(child.Component.Name, StringComparer.Ordinal))) throw new UiException("invalid_content", "Invalid children for " + component.Name);
        if (children.Count != 0 && (properties.ContainsKey("ItemsSource") || properties.ContainsKey("Content"))) throw new UiException("invalid_content", "Do not combine child elements with scalar Content or ItemsSource.");
        return new(key, component, properties.ToImmutable(), children.ToImmutable(), stateKey, actionId, when, each, itemKey) { Styles = CompileStyles(element), Context = context, ControlTemplate = CompileControlTemplate(element.Annotation<UiControlTemplateSource>()), ControlTheme = CompileControlTheme(element.Annotation<UiControlThemeSource>()) };
    }
    private UiValue Value(string text, UiProperty property, string bindingRoot = "data")
    {
        if (text.Length > Limits.TextCharacters) throw new UiException("text_limit", "Property text exceeds the configured limit.");
        if (UiBindingExpression.IsBinding(text)) return new(default, UiBindingExpression.Compile(text, bindingRoot, property, Limits));
        if (text.StartsWith("{}", StringComparison.Ordinal)) return new(property.ReadLiteral(text[2..]));
        if (text.StartsWith("{", StringComparison.Ordinal)) return new(default, Expression(text));
        return new(property.ReadLiteral(text));
    }
    private void ValidateAuthoringTemplate(XElement source)
    {
        var count = 0;
        Parse(source, "template", 0, new HashSet<string>(StringComparer.Ordinal), ref count);
    }
    private IUiExpression ScopedExpression(string source, string root) => UiBindingExpression.IsBinding(source)
        ? UiBindingExpression.Compile(source, root, null, Limits) : Expression(source);
    private UiValue ContextValue(string source, string root)
    {
        if (UiBindingExpression.IsBinding(source)) return new(default, UiBindingExpression.Compile(source, root, null, Limits));
        if (source == "{x:Null}") return new(UiJson.Element(null));
        if (source.StartsWith("{}", StringComparison.Ordinal)) return new(UiJson.Element(source[2..]));
        return source.StartsWith('{') ? new(default, Expression(source)) : new(UiJson.Element(source));
    }
    private IUiExpression Expression(string source)
    {
        const string prefix = "{ui:Expr ";
        if (!source.StartsWith(prefix, StringComparison.Ordinal) || !source.EndsWith('}')) throw new UiException("invalid_expression", "Use {ui:Expr <C# expression>}; arbitrary markup extensions require application compilation.");
        return CompileExpression(source[prefix.Length..^1]);
    }
}
