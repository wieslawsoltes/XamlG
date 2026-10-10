using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace XamlG.IntelligentUI.Avalonia;

/// <summary>Turns validated inert rules into real Avalonia styles, preserving framework
/// pseudoclasses, scoped cascading, theme precedence and local-value precedence.</summary>
internal static class UiAvaloniaStyles
{
    internal static ImmutableArray<Style> Create(ImmutableArray<UiStyleRule> rules, UiAvaloniaCatalog catalog)
    {
        var result = ImmutableArray.CreateBuilder<Style>();
        foreach (var rule in rules)
        {
            var parsed = UiStyles.ParseSelector(rule.Selector);
            if (!catalog.Registrations.TryGetValue(parsed.Target, out var registration))
                throw new UiException("invalid_style", "Style has no registered native target.");
            // Use the application's existing trusted converters. The detached probe never
            // receives handlers, source, state, actions, assets or an execution capability.
            var probe = registration.Create();
            try
            {
                var type = probe.GetType();
                var style = new Style(selector =>
                {
                    var current = selector.OfType(type);
                    if (parsed.Name != null) current = current.Name(parsed.Name);
                    foreach (var name in parsed.Classes) current = current.Class(name);
                    foreach (var name in parsed.PseudoClasses) current = current.Class(":" + name);
                    return current;
                });
                var properties = AvaloniaPropertyRegistry.Instance.GetRegistered(type);
                foreach (var pair in rule.Properties)
                {
                    if (!registration.Setters.TryGetValue(pair.Key, out var setter))
                        throw new UiException("invalid_style", "Style has no trusted native setter.");
                    var property = properties.FirstOrDefault(p => p.Name == pair.Key && !p.IsDirect);
                    if (property == null) throw new UiException("invalid_style", "Property is not an Avalonia styled property: " + pair.Key);
                    setter(probe, pair.Value);
                    style.Setters.Add(new Setter(property, probe.GetValue(property)));
                }
                result.Add(style);
            }
            finally { registration.Retire?.Invoke(probe); }
        }
        return result.ToImmutable();
    }
}
