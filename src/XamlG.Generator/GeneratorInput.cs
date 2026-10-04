using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Threading;
using XamlG.Compiler.Resources;

namespace XamlG.Generator;

internal sealed record GeneratorInput(string Path, string LogicalPath, string Text, bool Compile)
{
    public static GeneratorInput Read(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        var metadata = options.GetOptions(text);
        return new(text.Path, XamlInputMetadata.LogicalPath(text.Path, metadata, options.GlobalOptions),
            text.GetText(cancellationToken)?.ToString() ?? string.Empty, XamlInputMetadata.ShouldCompile(metadata));
    }
}
