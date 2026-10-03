using Microsoft.CodeAnalysis.Diagnostics;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Build-time defaults shared by the generator and workspace hosts. XML directives may override them per subtree.</summary>
public static class AvaloniaBuildOptions
{
    public const string XamlGCompiledBindingsProperty = "XamlGCompileBindingsByDefault";
    public const string AvaloniaCompiledBindingsProperty = "AvaloniaUseCompiledBindingsByDefault";
    private const string BuildPropertyPrefix = "build_property.";

    public static bool ReadCompileBindingsByDefault(AnalyzerConfigOptions options, bool fallback = true)
    {
        if (TryRead(options, XamlGCompiledBindingsProperty, out var configured)) return configured;
        if (TryRead(options, AvaloniaCompiledBindingsProperty, out configured)) return configured;
        return fallback;
    }

    private static bool TryRead(AnalyzerConfigOptions options, string property, out bool value)
    {
        value = default;
        return options.TryGetValue(BuildPropertyPrefix + property, out var text) && bool.TryParse(text, out value);
    }
}
