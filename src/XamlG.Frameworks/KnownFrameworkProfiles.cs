using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia;

namespace XamlG.Frameworks;

/// <summary>Host selection policy. Custom frameworks may supply their own immutable profile directly to the library.</summary>
public static class KnownFrameworkProfiles
{
    public static XamlFrameworkProfile Select(Compilation compilation, string? framework = null, bool compileBindingsByDefault = true)
    {
        if (string.IsNullOrWhiteSpace(framework) || string.Equals(framework, "Auto", StringComparison.OrdinalIgnoreCase))
            return compilation.GetTypeByMetadataName(AvaloniaMetadata.Object) != null
                ? AvaloniaFrameworkProfile.Create(compileBindingsByDefault)
                : XamlFrameworkProfile.Portable;

        if (string.Equals(framework, "Portable", StringComparison.OrdinalIgnoreCase))
            return XamlFrameworkProfile.Portable;
        if (string.Equals(framework, "Avalonia", StringComparison.OrdinalIgnoreCase))
            return AvaloniaFrameworkProfile.Create(compileBindingsByDefault);

        throw new ArgumentException($"Unknown framework profile '{framework}'. Supported built-in profiles: Auto, Portable, Avalonia.", nameof(framework));
    }
}
