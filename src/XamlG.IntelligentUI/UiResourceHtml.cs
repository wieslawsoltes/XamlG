using System.Text.RegularExpressions;

namespace XamlG.IntelligentUI;

/// <summary>Assembles trusted, embedded modules into a single self-contained MCP resource.
/// Includes are package-owned filenames, never model input or filesystem paths.</summary>
public static partial class UiResourceHtml
{
    private static readonly Lazy<string> Resource = new(() =>
    {
        var shell = Read("intelligent-ui.html");
        var result = Include().Replace(shell, match =>
        {
            var source = Read(match.Groups[1].Value);
            if (source.Contains("</script", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An embedded UI module contains an HTML script terminator.");
            return source;
        });
        if (result.Length > 1048576 || result.Contains("@include", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid embedded UI resource composition.");
        return result;
    });
    public static string Html => Resource.Value;
    private static string Read(string name)
    {
        using var stream = typeof(UiResourceHtml).Assembly.GetManifestResourceStream("XamlG.IntelligentUI.Resources." + name)
            ?? throw new InvalidOperationException("Embedded UI module is missing: " + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    [GeneratedRegex(@"/\* @include (ui-[a-z-]+\.js) \*/", RegexOptions.CultureInvariant)]
    private static partial Regex Include();
}
