using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Threading;

namespace XamlG.Generator;

internal sealed record GeneratorInput(string Path, string LogicalPath, string Text, bool Compile)
{
    public static GeneratorInput Read(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        var metadata = options.GetOptions(text);
        var logical = metadata.TryGetValue(GeneratorPropertyNames.LogicalPath, out var configured) ? configured : Normalize(text.Path);
        if (!metadata.TryGetValue(GeneratorPropertyNames.LogicalPath, out _) &&
            options.GlobalOptions.TryGetValue(GeneratorPropertyNames.ProjectDirectory, out var projectDirectory))
        {
            var prefix = Normalize(projectDirectory).TrimEnd('/') + "/";
            if (logical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                logical = logical.Substring(prefix.Length);
        }

        return new(text.Path, Normalize(logical), text.GetText(cancellationToken)?.ToString() ?? string.Empty,
            GeneratorOptions.ReadBoolean(metadata, GeneratorPropertyNames.Compile, true));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
