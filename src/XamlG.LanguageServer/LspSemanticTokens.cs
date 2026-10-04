using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

internal static class LspSemanticTokens
{
    public static readonly string[] Legend = { "class", "property", "event", "namespace", "string", "keyword", "comment", "typeParameter", "method" };

    public static int[] Encode(XamlAnalysis analysis, TextSpan? selection = null)
    {
        var spans = new List<(TextSpan Span, int Type, int Priority)>();
        foreach (var occurrence in analysis.Document.Symbols)
        {
            var kind = occurrence.Symbol switch { IPropertySymbol => 1, IEventSymbol => 2, IMethodSymbol { MethodKind: MethodKind.Constructor } => 0, IMethodSymbol => 8, ITypeParameterSymbol => 7, ITypeSymbol => 0, _ => 5 };
            spans.Add((occurrence.Span, kind, 0));
        }
        if (analysis.Syntax.Root != null)
            foreach (var element in analysis.Syntax.Root.DescendantsAndSelf())
            {
                spans.Add((element.NameSpan, 0, 1));
                if (element.EndNameSpan.Length > 0) spans.Add((element.EndNameSpan, 0, 1));
                foreach (var attribute in element.Attributes)
                {
                    spans.Add((attribute.NameSpan, attribute.IsNamespace ? 3 : 1, 1));
                    spans.Add((attribute.ValueSpan, 4, 2));
                }
                foreach (var trivia in element.Children.OfType<XamlTriviaSyntax>().Where(t => t.Kind == "Comment")) spans.Add((trivia.Span, 6, 1));
            }
        var result = new List<int>(); var previousLine = 0; var previousCharacter = 0; var previousEnd = -1;
        foreach (var token in spans.Where(t => t.Span.Length > 0).OrderBy(t => t.Span.Start).ThenBy(t => t.Priority).ThenBy(t => t.Span.Length))
        {
            if (token.Span.Start < previousEnd || token.Span.End > analysis.Syntax.Text.Length) continue;
            var cursor = token.Span.Start;
            while (cursor < token.Span.End)
            {
                var line = analysis.Syntax.Lines.GetPosition(cursor).Line;
                var next = line + 1 < analysis.Syntax.Lines.LineCount ? analysis.Syntax.Lines.GetOffset(new(line + 1, 0)) : analysis.Syntax.Text.Length;
                var end = Math.Min(next, token.Span.End);
                while (end > cursor && analysis.Syntax.Text[end - 1] is '\r' or '\n') end--;
                var startOffset = selection == null ? cursor : Math.Max(cursor, selection.Value.Start);
                var endOffset = selection == null ? end : Math.Min(end, selection.Value.End);
                if (endOffset > startOffset)
                {
                    var start = analysis.Syntax.Lines.GetPosition(startOffset);
                    var deltaLine = start.Line - previousLine;
                    result.Add(deltaLine); result.Add(deltaLine == 0 ? start.Character - previousCharacter : start.Character);
                    result.Add(endOffset - startOffset); result.Add(token.Type); result.Add(0);
                    previousLine = start.Line; previousCharacter = start.Character;
                }
                if (next <= cursor) break;
                cursor = next;
            }
            previousEnd = token.Span.End;
        }
        return result.ToArray();
    }
}
