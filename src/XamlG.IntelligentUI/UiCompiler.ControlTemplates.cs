using System.Collections.Immutable;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

internal sealed record UiControlTemplateSource(string Target, XElement Root);
internal sealed record UiControlThemeSource(string Target, XElement Setters, UiControlTemplateSource? Template, ImmutableArray<XElement> Styles);
internal sealed record UiPlanControlTemplateNode(string Type, ImmutableDictionary<string, UiValue> Properties,
    ImmutableDictionary<string, UiTemplateBinding> Bindings, ImmutableArray<UiPlanControlTemplateNode> Children, ImmutableArray<UiPlanStyle> Styles);
internal sealed record UiPlanControlTemplate(string Target, UiPlanControlTemplateNode Root);
internal sealed record UiPlanControlTheme(string Target, ImmutableDictionary<string, UiValue> Properties, UiPlanControlTemplate? Template, ImmutableArray<UiPlanStyle> Styles);

public sealed partial class UiCompiler
{
    private UiPlanControlTemplate? CompileControlTemplate(UiControlTemplateSource? source)
    {
        if (source is null) return null;
        var count = 0; var names = new HashSet<string>(StringComparer.Ordinal);
        UiPlanControlTemplateNode ParsePart(XElement element, int depth)
        {
            if (++count > Math.Min(Limits.Nodes, UiControlTemplates.MaximumNodes) || depth > Math.Min(Limits.Depth, UiControlTemplates.MaximumDepth))
                throw new UiException("node_limit", "Control template exceeds declaration limits.");
            if (element.Name.NamespaceName != UiCatalog.AvaloniaNamespace) throw new UiException("invalid_template", "Control templates contain native controls only.");
            if (element.Annotation<UiControlTemplateSource>() != null || element.Annotation<UiControlThemeSource>() != null)
                throw new UiException("invalid_template", "Nested control templates require separate owner declarations.");
            var component = Catalog.Get(element.Name.LocalName);
            var properties = ImmutableDictionary.CreateBuilder<string, UiValue>(StringComparer.Ordinal);
            var bindings = ImmutableDictionary.CreateBuilder<string, UiTemplateBinding>(StringComparer.Ordinal);
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name == XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) continue;
                if (attribute.Name.NamespaceName.Length != 0 || !component.Properties.TryGetValue(attribute.Name.LocalName, out var descriptor))
                    throw new UiException("invalid_template", "Template parts do not declare independent state, repetition or action authority.");
                var text = attribute.Value;
                if (text.StartsWith("{TemplateBinding ", StringComparison.Ordinal))
                {
                    if (!text.EndsWith('}')) throw new UiException("invalid_template", "Incomplete TemplateBinding.");
                    var parts = text[17..^1].Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length is < 1 or > 2 || parts[0].Length == 0) throw new UiException("invalid_template", "Invalid TemplateBinding.");
                    var name = parts[0].StartsWith("Property=", StringComparison.Ordinal) ? parts[0][9..].Trim() : parts[0];
                    var mode = parts.Length == 1 ? "OneWay" : parts[1].StartsWith("Mode=", StringComparison.Ordinal) ? parts[1][5..].Trim() : "";
                    bindings.Add(attribute.Name.LocalName, new(name, mode));
                }
                else
                {
                    if (UiBindingExpression.IsBinding(text) && UiBindingExpression.Parse(text, "data", Limits).Mode == "TwoWay")
                        throw new UiException("invalid_template", "Use TemplateBinding for native input writeback.");
                    properties.Add(attribute.Name.LocalName, Value(text, descriptor, element.Annotation<UiBindingScope>()?.Root ?? "data"));
                }
            }
            var literal = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
            if (!string.IsNullOrWhiteSpace(literal))
            {
                if (component.TextProperty is not { } textProperty || properties.ContainsKey(textProperty) || bindings.ContainsKey(textProperty)) throw new UiException("invalid_template", "Conflicting template content.");
                properties.Add(textProperty, Value(literal, component.Properties[textProperty]));
            }
            if (properties.TryGetValue("Name", out var nameValue))
            {
                if (nameValue.Expression != null || !names.Add(nameValue.Literal.GetString()!)) throw new UiException("invalid_template", "Template names must be unique literals.");
            }
            return new(component.Name, properties.ToImmutable(), bindings.ToImmutable(), element.Elements().Select(child => ParsePart(child, depth + 1)).ToImmutableArray(), CompileStyles(element));
        }
        var root = ParsePart(source.Root, 0);
        UiControlTemplateNode Literals(UiPlanControlTemplateNode part) => new(part.Type,
            part.Properties.Where(p => p.Value.Expression is null).ToImmutableDictionary(p => p.Key, p => p.Value.Literal, StringComparer.Ordinal),
            part.Bindings, part.Children.Select(Literals).ToImmutableArray());
        UiControlTemplates.Validate(new UiControlTemplate(source.Target, Literals(root)), source.Target, Catalog);
        return new(source.Target, root);
    }
    private UiPlanControlTheme? CompileControlTheme(UiControlThemeSource? source)
    {
        if (source is null) return null;
        var component = Catalog.Get(source.Target);
        var properties = ImmutableDictionary.CreateBuilder<string, UiValue>(StringComparer.Ordinal);
        foreach (var setter in source.Setters.Elements())
        {
            var name = (string)setter.Attribute("Property")!; UiStyles.ValidateProperty(component, name);
            properties.Add(name, Value((string)setter.Attribute("Value")!, component.Properties[name]));
        }
        var holder = new XElement(XName.Get(source.Target, UiCatalog.AvaloniaNamespace));
        holder.AddAnnotation(new UiStyleSources(source.Styles));
        return new(source.Target, properties.ToImmutable(), CompileControlTemplate(source.Template), CompileStyles(holder));
    }
}
