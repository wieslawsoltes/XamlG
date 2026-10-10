using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XamlG.IntelligentUI;

/// <summary>Inert, scoped presentation styles. These do not define inputs, actions or execution authority.</summary>
public sealed record UiStyleRule(string Selector, ImmutableDictionary<string, JsonElement> Properties);
public sealed record UiStyleSelector(string Target, string? Name, ImmutableArray<string> Classes, ImmutableArray<string> PseudoClasses)
{
    public ImmutableArray<UiSelectorStep> Steps { get; init; } = [];
}
internal sealed record UiPlanStyle(string Selector, UiComponent Target, ImmutableDictionary<string, UiValue> Properties);

/// <summary>A bounded selector and property vocabulary shared by compilation, native rendering and export.</summary>
public static class UiStyles
{
    public const int MaximumRules = 128;
    public const int MaximumSetters = 64;
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Pseudos = new(StringComparer.Ordinal)
    { "pointerover", "pressed", "focus", "focus-within", "focus-visible", "disabled", "checked", "unchecked", "indeterminate", "selected", "expanded" };
    // Behavioral properties are deliberately absent. A visual style must not change a state
    // input or make the native action's availability disagree with the owning session store.
    private static readonly HashSet<string> PresentationProperties = new(StringComparer.Ordinal)
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "Opacity",
        "Background", "Foreground", "BorderBrush", "BorderThickness", "Padding", "CornerRadius",
        "FontFamily", "FontSize", "FontStyle", "FontWeight", "TextAlignment", "TextWrapping",
        "TextTrimming", "MaxLines", "LineHeight", "HorizontalAlignment", "VerticalAlignment",
        "HorizontalContentAlignment", "VerticalContentAlignment", "RenderTransform", "RenderTransformOrigin",
        "Clip", "Fill", "Stroke", "StrokeThickness", "StrokeDashArray", "StrokeDashOffset", "StrokeLineCap",
        "StrokeJoin", "StrokeMiterLimit", "Stretch", "RadiusX", "RadiusY"
    };
    public static bool IsIdentifier(string value) => value is not null && IdentifierPattern.IsMatch(value);
    public static ImmutableArray<string> ReadClasses(string value)
    {
        if (value is null || value.Length > 2048) throw Invalid("Class list exceeds its budget.");
        var values = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length > 32 || values.Any(item => !IsIdentifier(item)) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Invalid("Use up to 32 distinct class identifiers, separated by whitespace.");
        return values.ToImmutableArray();
    }
    public static UiStyleSelector ParseSelector(string source) => UiStyleSelectors.Parse(source);
    public static bool IsPseudoClass(string name) => Pseudos.Contains(name);
    public static void ValidateProperty(UiComponent target, string name)
    {
        if (!PresentationProperties.Contains(name) || !target.Properties.ContainsKey(name) || target.InputProperty == name)
            throw Invalid("Style property is not a declared presentation property: " + target.Name + "." + name);
    }
    public static void Validate(ImmutableArray<UiStyleRule> styles, UiCatalog catalog, int textLimit = 16384)
    {
        if (styles.IsDefault || styles.Length > MaximumRules) throw Invalid("Invalid style collection.");
        var setters = 0;
        foreach (var rule in styles)
        {
            if (rule is null || rule.Properties is null || rule.Properties.Count > MaximumSetters || (setters += rule.Properties.Count) > 512)
                throw Invalid("Style setter budget exceeded.");
            var selector = ParseSelector(rule.Selector); UiStyleSelectors.ValidateTypes(selector, catalog);
            var target = catalog.Get(selector.Target);
            foreach (var property in rule.Properties) ValidateProperty(target, property.Key);
            // Reuse all cross-property/geometry/text checks, not only the individual JSON kinds.
            UiTreeValidation.ValidateElement(new("style", target.Name, rule.Properties, []), target, textLimit);
        }
    }
    public static bool Equivalent(ImmutableArray<UiStyleRule> left, ImmutableArray<UiStyleRule> right)
    {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Selector != right[i].Selector || left[i].Properties.Count != right[i].Properties.Count) return false;
            foreach (var pair in left[i].Properties)
                if (!right[i].Properties.TryGetValue(pair.Key, out var value) || !JsonElement.DeepEquals(pair.Value, value)) return false;
        }
        return true;
    }
    private static UiException Invalid(string message) => new("invalid_style", message);
}
