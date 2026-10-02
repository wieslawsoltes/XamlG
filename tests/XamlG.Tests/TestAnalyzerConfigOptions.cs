using Microsoft.CodeAnalysis.Diagnostics;

namespace XamlG.Tests;

internal sealed class TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string>? values = null) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        if (values != null && values.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
