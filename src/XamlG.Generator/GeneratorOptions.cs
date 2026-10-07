using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Frameworks.Avalonia;

namespace XamlG.Generator;

internal sealed record GeneratorOptions(bool Enabled, string Framework, string ProjectDirectory, bool GenerateInitializeComponent, bool GenerateNamedFields, bool CompileBindingsByDefault, bool CreateSourceInfo)
{
    public static GeneratorOptions Read(AnalyzerConfigOptions options) => new(
        ReadBoolean(options, GeneratorPropertyNames.Enabled, true),
        ReadString(options, GeneratorPropertyNames.Framework, "Auto"),
        ReadString(options, GeneratorPropertyNames.ProjectDirectory, string.Empty),
        ReadBoolean(options, GeneratorPropertyNames.GenerateInitializeComponent, true),
        ReadBoolean(options, GeneratorPropertyNames.GenerateNamedFields, true),
        AvaloniaBuildOptions.ReadCompileBindingsByDefault(options),
        AvaloniaBuildOptions.ReadCreateSourceInfo(options));

    internal static bool ReadBoolean(AnalyzerConfigOptions options, string name, bool fallback) =>
        options.TryGetValue(name, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static string ReadString(AnalyzerConfigOptions options, string name, string fallback) =>
        options.TryGetValue(name, out var value) ? value : fallback;
}
