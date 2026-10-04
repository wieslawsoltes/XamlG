using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Threading;

namespace XamlG.Generator;

internal sealed record GeneratorInput(string Path, string LogicalPath, string Text, bool Compile)
{
    public static GeneratorInput Read(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        var metadata = options.GetOptions(text);
        // CompilerVisibleItemMetadata emits empty entries for items contributed by other
        // build targets. Presence alone does not make that entry a valid logical path.
        var logical = metadata.TryGetValue(GeneratorPropertyNames.LogicalPath, out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? Normalize(configured) : Normalize(text.Path);
        if (options.GlobalOptions.TryGetValue(GeneratorPropertyNames.ProjectDirectory, out var projectDirectory) && !string.IsNullOrWhiteSpace(projectDirectory))
        {
            var prefix = Normalize(projectDirectory).TrimEnd('/') + "/";
            // Windows drive/UNC paths are case-insensitive. Unix source identities are not.
            var comparison = prefix.StartsWith("//", StringComparison.Ordinal) || prefix.Length > 1 && prefix[1] == ':'
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (logical.StartsWith(prefix, comparison)) logical = logical.Substring(prefix.Length);
        }
        while (logical.StartsWith("./", StringComparison.Ordinal)) logical = logical.Substring(2);
        return new(text.Path, logical, text.GetText(cancellationToken)?.ToString() ?? string.Empty,
            GeneratorOptions.ReadBoolean(metadata, GeneratorPropertyNames.Compile, true));
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
