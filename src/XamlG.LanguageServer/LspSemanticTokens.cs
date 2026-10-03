using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

internal static class LspSemanticTokens
{
    public static readonly string[] Legend = { "class", "property", "event", "namespace", "string", "keyword", "comment", "typeParameter", "method" };

    public static int[] Encode(XamlAnalysis analysis)
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
            var start = analysis.Syntax.Lines.GetPosition(token.Span.Start);
            var end = analysis.Syntax.Lines.GetPosition(token.Span.End);
            if (start.Line != end.Line) continue;
            var deltaLine = start.Line - previousLine;
            result.Add(deltaLine); result.Add(deltaLine == 0 ? start.Character - previousCharacter : start.Character);
            result.Add(token.Span.Length); result.Add(token.Type); result.Add(0);
            previousLine = start.Line; previousCharacter = start.Character; previousEnd = token.Span.End;
        }
        return result.ToArray();
    }
}
