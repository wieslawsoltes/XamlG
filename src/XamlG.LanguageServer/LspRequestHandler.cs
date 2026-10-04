using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

internal sealed class LspRequestHandler(XamlCompilationSession compiler, LspDocumentStore documents)
{
    public object? Handle(string method, JsonElement parameters, CancellationToken cancellationToken, LspDocumentSetSnapshot? captured = null)
    {
        captured ??= documents.Capture();
        var uri = LspConversions.DocumentUri(parameters);
        var document = captured.Documents.FirstOrDefault(d => d.Uri == uri) ?? throw new LspRequestException(-32602, "The document is not open.");
        var analyses = compiler.AnalyzeOverlays(captured.Documents.Select(d => d.Syntax), cancellationToken);
        var analysis = analyses.Single(a => ReferenceEquals(a.Syntax, document.Syntax));
        var language = new XamlLanguageService(compiler);
        var position = parameters.TryGetProperty("position", out var point) ? documents.GetOffset(document, LspConversions.Position(point)) : 0;
        object? result = method switch
        {
            LspMethods.Hover => Hover(language, analysis, position, cancellationToken),
            LspMethods.Completion => new { isIncomplete = false, items = language.GetCompletions(analysis, position, CompletionKind(document.Syntax, position), cancellationToken).Select(c => new { label = c.Label, insertText = c.InsertText, detail = c.Detail, kind = c.Kind switch { "Class" => 7, "Property" => 10, "Event" => 23, "EnumMember" => 20, _ => 12 } }).ToArray() },
            LspMethods.Definition => language.GetDefinitions(analysis, position).Select(d => new { uri = LspConversions.UriForPath(d.Path), range = new LspRange(new(d.Start.Line, d.Start.Character), new(d.End.Line, d.End.Character)) }).ToArray(),
            LspMethods.Highlights => language.GetReferences(analysis, position).Select(span => new { range = LspConversions.Range(document.Syntax, span), kind = 1 }).ToArray(),
            LspMethods.References => References(analysis, position, captured, analyses),
            LspMethods.Symbols => document.Syntax.Root == null ? Array.Empty<object>() : new[] { Symbol(document.Syntax.Root, document.Syntax) },
            LspMethods.Folding => document.Syntax.Root?.DescendantsAndSelf().Select(e => new { startLine = document.Syntax.Lines.GetPosition(e.OpenTagSpan.End).Line, endLine = document.Syntax.Lines.GetPosition(e.CloseTagSpan.Start).Line, kind = "region" }).Where(r => r.endLine > r.startLine).ToArray() ?? Array.Empty<object>(),
            LspMethods.SemanticTokens => new { resultId = captured.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), data = LspSemanticTokens.Encode(analysis) },
            LspMethods.Inspect => new { syntax = XamlInspector.Syntax(analysis.Syntax), bound = XamlInspector.Bound(analysis.Document), generated = analysis.Output.Source, sourceMappings = analysis.Output.SourceMappings },
            _ => throw new LspRequestException(-32601, "The requested method is not supported: " + method)
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (!documents.IsCurrent(captured)) throw new LspRequestException(-32801, "An open document changed while the request was being processed.");
        return result;
    }
    private static object? Hover(XamlLanguageService language, XamlAnalysis analysis, int position, CancellationToken cancellationToken)
    {
        var hover = language.GetHover(analysis, position, cancellationToken);
        return hover == null ? null : new { contents = new { kind = "plaintext", value = hover.Signature + (hover.Documentation.Length == 0 ? string.Empty : "\n\n" + hover.Documentation) }, range = LspConversions.Range(analysis.Syntax, hover.Span) };
    }
    private static object[] References(XamlAnalysis analysis, int position, LspDocumentSetSnapshot snapshot, ImmutableArray<XamlAnalysis> analyses)
    {
        var symbol = analysis.Document.Symbols.Where(s => s.Span.Contains(position)).OrderBy(s => s.Span.Length).FirstOrDefault()?.Symbol;
        if (symbol == null) return Array.Empty<object>();
        var result = new List<object>();
        foreach (var document in snapshot.Documents)
        {
            var bound = analyses.Single(a => ReferenceEquals(a.Syntax, document.Syntax));
            foreach (var span in bound.Document.Symbols.Where(s => SymbolEqualityComparer.Default.Equals(symbol, s.Symbol)).Select(s => s.Span).Distinct())
                result.Add(new { uri = document.Uri, range = LspConversions.Range(document.Syntax, span) });
        }
        return result.ToArray();
    }
    private static object Symbol(XamlElementSyntax element, XamlSyntaxTree syntax) => new
    {
        name = element.Name, kind = element.LocalName.Contains('.') ? 7 : 19,
        range = LspConversions.Range(syntax, element.Span), selectionRange = LspConversions.Range(syntax, element.NameSpan),
        children = element.Children.OfType<XamlElementSyntax>().Select(child => Symbol(child, syntax)).ToArray()
    };
    private static XamlCompletionKind CompletionKind(XamlSyntaxTree syntax, int position)
    {
        var element = syntax.FindElement(position);
        if (element?.Attributes.Any(a => a.ValueSpan.Start <= position && position <= a.ValueSpan.End) == true) return XamlCompletionKind.Value;
        return element == null || position <= element.NameSpan.End ? XamlCompletionKind.Element : XamlCompletionKind.Attribute;
    }
}
