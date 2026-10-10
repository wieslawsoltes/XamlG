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
        var compile = GeneratorOptions.ReadBoolean(options.GlobalOptions, GeneratorPropertyNames.Enabled, true) &&
            XamlInputMetadata.ShouldCompile(metadata);
        return new(text.Path, XamlInputMetadata.LogicalPath(text.Path, metadata, options.GlobalOptions),
            compile ? text.GetText(cancellationToken)?.ToString() ?? string.Empty : string.Empty, compile);
    }
}
