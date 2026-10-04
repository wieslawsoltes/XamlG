using System.Collections.Immutable;
using System.Text.Json;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Formatting;
using XamlG.Tooling.Refactoring;

namespace XamlG.LanguageServer;

internal sealed class LspEditingRequests(XamlCompilationSession compiler, LspDocumentStore documents, bool versionedEdits)
{
    public object? Handle(string method, JsonElement parameters, LspDocumentSnapshot document,
        XamlAnalysis analysis, ImmutableArray<XamlAnalysis> workspace, CancellationToken token)
    {
        var position = parameters.TryGetProperty("position", out var point) ? documents.GetOffset(document, LspConversions.Position(point)) : 0;
        switch (method)
        {
            case LspMethods.PrepareRename:
                var prepared = new XamlRenameService(compiler).Prepare(analysis, position, token);
                return prepared == null ? null : new { range = LspConversions.Range(document.Syntax, prepared.Span), placeholder = prepared.Name };
            case LspMethods.Rename:
                var plan = new XamlRenameService(compiler).Rename(analysis, position, parameters.GetProperty("newName").GetString()!, workspace, token);
                return WorkspaceEdit(plan.Documents, document);
            case LspMethods.Formatting:
            case LspMethods.RangeFormatting:
                var range = method == LspMethods.RangeFormatting ? Selection(parameters.GetProperty("range"), document) : (TextSpan?)null;
                return TextEdits(document.Syntax.Text, XamlFormatter.Format(document.Syntax, Options(parameters), range, analysis, token));
            case LspMethods.CodeAction:
                var selection = Selection(parameters.GetProperty("range"), document);
                var only = parameters.TryGetProperty("context", out var context) && context.TryGetProperty("only", out var kinds)
                    ? kinds.EnumerateArray().Select(k => k.GetString()!).ToArray() : Array.Empty<string>();
                return new XamlCodeActionService(compiler).GetActions(analysis, selection, cancellationToken: token)
                    .Where(action => only.Length == 0 || only.Any(k => action.Kind == k || action.Kind.StartsWith(k + ".", StringComparison.Ordinal)))
                    .Select(action => new { title = action.Title, kind = action.Kind, isPreferred = action.IsPreferred,
                        edit = WorkspaceEdit(ImmutableArray.Create(new XamlDocumentEdits(document.Syntax.Path, document.Syntax.Text, document.Version, action.Changes)), document) }).ToArray();
            default: throw new LspRequestException(-32601, "The editing method is not supported.");
        }
    }
    public TextSpan Selection(JsonElement value, LspDocumentSnapshot document)
    {
        var range = LspConversions.Range(value);
        var start = documents.GetOffset(document, range.Start); var end = documents.GetOffset(document, range.End);
        if (end < start) throw new LspRequestException(-32602, "The requested range is reversed.");
        return TextSpan.FromBounds(start, end);
    }
    private static XamlFormattingOptions Options(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("options", out var options)) return new();
        return new()
        {
            TabSize = options.TryGetProperty("tabSize", out var size) ? size.GetInt32() : 4,
            InsertSpaces = !options.TryGetProperty("insertSpaces", out var spaces) || spaces.GetBoolean(),
            InsertFinalNewline = options.TryGetProperty("insertFinalNewline", out var final) && final.GetBoolean()
        };
    }
    private object WorkspaceEdit(ImmutableArray<XamlDocumentEdits> edits, LspDocumentSnapshot current)
    {
        string Uri(string path) => path == current.Syntax.Path ? current.Uri : LspConversions.UriForPath(path);
        if (versionedEdits)
            return new { documentChanges = edits.Select(edit => new { textDocument = new { uri = Uri(edit.Path), version = edit.Version },
                edits = TextEdits(edit.OriginalText, edit.Changes) }).ToArray() };
        return new { changes = edits.ToDictionary(edit => Uri(edit.Path), edit => TextEdits(edit.OriginalText, edit.Changes), StringComparer.Ordinal) };
    }
    private static object[] TextEdits(string source, ImmutableArray<XamlTextChange> edits)
    {
        var lines = new SourceLineMap(source);
        return edits.Select(edit =>
        {
            var start = lines.GetPosition(edit.Span.Start); var end = lines.GetPosition(edit.Span.End);
            return (object)new { range = new LspRange(new(start.Line, start.Character), new(end.Line, end.Character)), newText = edit.NewText };
        }).ToArray();
    }
}
