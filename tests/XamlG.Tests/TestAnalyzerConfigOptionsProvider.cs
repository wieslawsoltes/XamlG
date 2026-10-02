using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XamlG.Tests;

internal sealed class TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string>? globals = null) : AnalyzerConfigOptionsProvider
{
    private static readonly AnalyzerConfigOptions Empty = new TestAnalyzerConfigOptions();
    public override AnalyzerConfigOptions GlobalOptions { get; } = new TestAnalyzerConfigOptions(globals);
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
}
