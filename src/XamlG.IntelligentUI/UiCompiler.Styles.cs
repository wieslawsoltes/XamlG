using System.Collections.Immutable;
using System.Xml.Linq;

namespace XamlG.IntelligentUI;

public sealed partial class UiCompiler
{
    private IUiExpression RepeatExpression(string source, string bindingRoot = "data")
    {
        if (source.StartsWith('{')) return ScopedExpression(source, bindingRoot);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || document.RootElement.GetArrayLength() > Limits.Nodes)
                throw new UiException("invalid_repeat", "Literal repetition requires a bounded array.");
            return new ConstantArray(source, document.RootElement.Clone());
        }
        catch (System.Text.Json.JsonException) { throw new UiException("invalid_repeat", "Invalid literal repeat array."); }
    }
    private sealed record ConstantArray(string Source, System.Text.Json.JsonElement Value) : IUiExpression
    {
        public System.Text.Json.JsonElement Evaluate(System.Text.Json.JsonElement state, System.Text.Json.JsonElement data, System.Text.Json.JsonElement? item = null) => Value;
    }
    private ImmutableArray<UiPlanStyle> CompileStyles(XElement element)
    {
        var result = ImmutableArray.CreateBuilder<UiPlanStyle>();
        foreach (var source in element.Annotation<UiStyleSources>()?.Rules ?? [])
        {
            var selector = (string?)source.Attribute("Selector") ?? throw new UiException("invalid_style", "Style requires Selector.");
            var target = Catalog.Get(UiStyles.ParseSelector(selector).Target);
            var properties = ImmutableDictionary.CreateBuilder<string, UiValue>(StringComparer.Ordinal);
            foreach (var setter in source.Elements())
            {
                var name = (string?)setter.Attribute("Property") ?? throw new UiException("invalid_style", "Setter requires Property.");
                UiStyles.ValidateProperty(target, name);
                if (!properties.TryAdd(name, Value((string?)setter.Attribute("Value") ?? "", target.Properties[name], element.Annotation<UiBindingScope>()?.Root ?? "data")))
                    throw new UiException("invalid_style", "Duplicate setter property.");
            }
            result.Add(new(selector, target, properties.ToImmutable()));
        }
        return result.ToImmutable();
    }
}
internal sealed record UiStyleSources(ImmutableArray<XElement> Rules);
