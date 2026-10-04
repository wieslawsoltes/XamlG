using System.Threading;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Locates only source-bearing members declared by the selected framework profile.
/// Lexical ranges retain quotes, surrounding whitespace and unrelated trivia.</summary>
internal static class XamlResourceSourceReader
{
    public static IEnumerable<XamlResourceSourceSite> Read(XamlAnalysis analysis, XamlCompilationSession compiler, CancellationToken token)
    {
        if (analysis.Syntax.Root == null) yield break;
        var pending = new Stack<(XamlElementSyntax Element, NamespaceScope Parent)>();
        pending.Push((analysis.Syntax.Root, NamespaceScope.Empty));
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            var entry = pending.Pop();
            var element = entry.Element; var scope = entry.Parent.Push(element);
            foreach (var child in element.Children.OfType<XamlElementSyntax>().Reverse()) pending.Push((child, scope));
            var name = scope.Expand(element.Name);
            var type = name.Namespace == null ? null : compiler.Types.Resolve(name.Namespace, name.LocalName).Type;
            if (type == null || !compiler.Profile.ResourceSourceMembers.TryGetValue(type.MetadataName(), out var member)) continue;
            foreach (var attribute in element.Attributes.Where(a => a.Name == member))
                yield return new(attribute.Value, attribute.ValueSpan, attribute.Quote, true);
            foreach (var property in element.Children.OfType<XamlElementSyntax>().Where(e => e.LocalName.EndsWith("." + member, StringComparison.Ordinal)))
            {
                var texts = property.Children.OfType<XamlTextSyntax>().ToArray();
                var decoded = string.Concat(texts.Select(t => t.Value)).Trim();
                if (texts.Length == 1 && !texts[0].IsCData && !property.Children.OfType<XamlElementSyntax>().Any())
                {
                    var span = texts[0].Span;
                    var raw = analysis.Syntax.Text.Substring(span.Start, span.Length);
                    var start = 0; var end = raw.Length;
                    while (start < end && char.IsWhiteSpace(raw[start])) start++;
                    while (end > start && char.IsWhiteSpace(raw[end - 1])) end--;
                    yield return new(decoded, new TextSpan(span.Start + start, end - start), null, true);
                }
                else yield return new(decoded, property.Span, null, false);
            }
        }
    }
}
