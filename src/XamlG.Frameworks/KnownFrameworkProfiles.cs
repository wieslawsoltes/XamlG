using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia;

namespace XamlG.Frameworks;

/// <summary>Host selection policy. Custom frameworks may supply their own immutable profile directly to the library.</summary>
public static class KnownFrameworkProfiles
{
    public static XamlFrameworkProfile Select(Compilation compilation, string? framework = null)
    {
        if (string.IsNullOrWhiteSpace(framework) || framework.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return compilation.GetTypeByMetadataName(AvaloniaMetadata.Object) != null
                ? AvaloniaFrameworkProfile.Create()
                : XamlFrameworkProfile.Portable;

        if (framework.Equals("Portable", StringComparison.OrdinalIgnoreCase))
            return XamlFrameworkProfile.Portable;
        if (framework.Equals("Avalonia", StringComparison.OrdinalIgnoreCase))
            return AvaloniaFrameworkProfile.Create();

        throw new ArgumentException($"Unknown framework profile '{framework}'. Supported built-in profiles: Auto, Portable, Avalonia.", nameof(framework));
    }
}
