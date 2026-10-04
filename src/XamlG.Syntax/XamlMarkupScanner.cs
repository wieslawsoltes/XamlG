using System.Threading;

namespace XamlG.Syntax;

public static class XamlMarkupScanner
{
    public static IEnumerable<XamlMarkupOccurrence> Scan(XamlSyntaxTree tree, XamlAttributeSyntax attribute,
        CancellationToken cancellationToken = default)
    {
        if (attribute.IsNamespace || !attribute.Value.StartsWith("{", StringComparison.Ordinal) || attribute.Value.StartsWith("{}", StringComparison.Ordinal)) yield break;
        XamlDecodedTextMap map;
        try { map = XamlDecodedTextMap.Create(tree.Text, attribute.ValueSpan); }
        catch (ArgumentException) { yield break; }
        if (map.Text != attribute.Value) yield break;
        var pending = new Stack<(string Text, int Start, int Depth)>();
        pending.Push((map.Text, 0, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, start, depth) = pending.Pop();
            if (depth > 64) continue;
            var errors = new List<XamlDiagnostic>();
            var syntax = MarkupExtensionParser.Parse(text, new(start, text.Length), errors.Add);
            if (syntax == null || errors.Count != 0) continue;
            yield return new(syntax, map);
            foreach (var argument in syntax.Arguments.Reverse())
                if (argument.ValueSpan is { } span && argument.Value.StartsWith("{", StringComparison.Ordinal) &&
                    map.Text.Substring(span.Start, span.Length) == argument.Value)
                    pending.Push((argument.Value, span.Start, depth + 1));
        }
    }
}
