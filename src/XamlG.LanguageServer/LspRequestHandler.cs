using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Navigation;
using XamlG.Tooling.Refactoring;

namespace XamlG.LanguageServer;

internal sealed class LspRequestHandler(XamlCompilationSession compiler, LspDocumentStore documents,
    LspSemanticTokenCache? tokenCache = null, bool versionedEdits = false)
{
    public object? Handle(string method, JsonElement parameters, CancellationToken cancellationToken,
        LspDocumentSetSnapshot? captured = null, ImmutableArray<XamlAnalysis> workspace = default)
    {
        captured ??= documents.Capture();
        if (workspace.IsDefault) workspace = compiler.AnalyzeWorkspace(captured.Documents.Select(d => d.Syntax), cancellationToken);
        if (method == LspMethods.WorkspaceSymbols)
        {
            var symbols = WorkspaceSymbols(parameters, workspace, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!documents.IsCurrent(captured)) throw new LspRequestException(-32801, "The open buffers changed during workspace-symbol discovery.");
            return symbols;
        }
        var uri = LspConversions.DocumentUri(parameters);
        var document = captured.Documents.FirstOrDefault(d => d.Uri == uri) ?? throw new LspRequestException(-32602, "The document is not open.");
        var analysis = workspace.Single(a => ReferenceEquals(a.Syntax, document.Syntax));
        var language = new XamlLanguageService(compiler);
        var editing = new LspEditingRequests(compiler, documents, versionedEdits);
        var resources = new XamlResourceLanguageService(compiler);
        var names = XamlNameReferenceIndex.Create(analysis, compiler, cancellationToken);
        var position = parameters.TryGetProperty("position", out var point) ? documents.GetOffset(document, LspConversions.Position(point)) : 0;
        object? result = method switch
        {
            LspMethods.Hover => Hover(language, analysis, names, resources, position, cancellationToken),
            LspMethods.Completion => new { isIncomplete = false, items = resources.GetCompletions(analysis, position)
                .Concat(language.GetCompletions(analysis, position, CompletionKind(document.Syntax, position), cancellationToken))
                .GroupBy(c => c.Label, StringComparer.Ordinal).Select(g => g.First()).Select(c => new { label = c.Label, insertText = c.InsertText,
                    detail = c.Detail, kind = c.Kind switch { "Class" => 7, "Property" => 10, "Event" => 23, "EnumMember" => 20, "File" => 17, _ => 12 } }).ToArray() },
            LspMethods.Definition => Definitions(language, analysis, names, resources, position, workspace),
            LspMethods.Highlights => (names.At(position) is { } named ? names.References(named).Select(o => o.Span) : language.GetReferences(analysis, position))
                .Select(span => new { range = LspConversions.Range(document.Syntax, span), kind = 1 }).ToArray(),
            LspMethods.References => References(analysis, position, names, resources, workspace, parameters, cancellationToken),
            LspMethods.Symbols => document.Syntax.Root == null ? Array.Empty<object>() : new[] { Symbol(document.Syntax.Root, document.Syntax) },
            LspMethods.Folding => document.Syntax.Root?.DescendantsAndSelf().Select(e => new { startLine = document.Syntax.Lines.GetPosition(e.OpenTagSpan.End).Line,
                endLine = document.Syntax.Lines.GetPosition(e.CloseTagSpan.Start).Line, kind = "region" }).Where(r => r.endLine > r.startLine).ToArray() ?? Array.Empty<object>(),
            LspMethods.SemanticTokens => (tokenCache ?? new()).GetResult(uri, LspSemanticTokens.Encode(analysis)),
            LspMethods.SemanticTokensDelta => (tokenCache ?? new()).GetResult(uri, LspSemanticTokens.Encode(analysis), parameters.GetProperty("previousResultId").GetString()),
            LspMethods.SemanticTokensRange => new { data = LspSemanticTokens.Encode(analysis, editing.Selection(parameters.GetProperty("range"), document)) },
            LspMethods.DocumentLinks => resources.GetReferences(analysis).Where(r => r.TargetPath != null).Select(r => new {
                range = LspConversions.Range(document.Syntax, r.Span), target = LspConversions.UriForPath(r.TargetPath!), tooltip = r.RootType + " · " + r.ResourceUri }).ToArray(),
            LspMethods.PrepareRename or LspMethods.Rename or LspMethods.Formatting or LspMethods.RangeFormatting or LspMethods.CodeAction =>
                editing.Handle(method, parameters, document, analysis, workspace, cancellationToken),
            LspMethods.Inspect => new { syntax = XamlInspector.Syntax(analysis.Syntax), bound = XamlInspector.Bound(analysis.Document), generated = analysis.Output.Source,
                sourceMappings = analysis.Output.SourceMappings },
            _ => throw new LspRequestException(-32601, "The requested method is not supported: " + method)
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (!documents.IsCurrent(captured)) throw new LspRequestException(-32801, "An open document changed while the request was being processed.");
        return result;
    }
    private static object? Hover(XamlLanguageService language, XamlAnalysis analysis, XamlNameReferenceIndex names,
        XamlResourceLanguageService resources, int position, CancellationToken token)
    {
        string? text = null; TextSpan span = default;
        if (names.At(position) is { } name && names.Declaration(name) is { } declaration)
        { text = declaration.Type?.ToDisplayString() + " " + declaration.Name + " (namescope " + declaration.NameScopeId + ")"; span = name.Span; }
        else if (resources.GetReferences(analysis).FirstOrDefault(r => r.Span.Contains(position)) is { } resource)
        { text = resource.RootType + "\n" + resource.ResourceUri + (resource.IsExternal ? "\nReferenced compiled factory" : "\nProject compiled factory"); span = resource.Span; }
        else if (language.GetHover(analysis, position, token) is { } hover)
        { text = hover.Signature + (hover.Documentation.Length == 0 ? string.Empty : "\n\n" + hover.Documentation); span = hover.Span; }
        return text == null ? null : new { contents = new { kind = "plaintext", value = text }, range = LspConversions.Range(analysis.Syntax, span) };
    }
    private static object[] Definitions(XamlLanguageService language, XamlAnalysis analysis, XamlNameReferenceIndex names,
        XamlResourceLanguageService resources, int position, ImmutableArray<XamlAnalysis> workspace)
    {
        if (names.At(position) is { } name && names.Declaration(name) is { } declaration)
            return new[] { Location(analysis.Syntax, declaration.Span) };
        var reference = resources.GetReferences(analysis).FirstOrDefault(r => r.Span.Contains(position));
        if (reference?.TargetPath != null && workspace.FirstOrDefault(a => a.Syntax.Path == reference.TargetPath) is { } target)
            return new[] { Location(target.Syntax, target.Syntax.Root?.NameSpan ?? new(0, 0)) };
        return language.GetDefinitions(analysis, position).Select(d => (object)new { uri = LspConversions.UriForPath(d.Path),
            range = new LspRange(new(d.Start.Line, d.Start.Character), new(d.End.Line, d.End.Character)) }).ToArray();
    }
    private object[] References(XamlAnalysis analysis, int position, XamlNameReferenceIndex names,
        XamlResourceLanguageService resources, ImmutableArray<XamlAnalysis> workspace, JsonElement parameters, CancellationToken token)
    {
        var includeDeclaration = parameters.TryGetProperty("context", out var context) && context.TryGetProperty("includeDeclaration", out var include) && include.GetBoolean();
        var csharp = new XamlCSharpReferenceService(compiler);
        if (names.At(position) is { } named)
        {
            var locations = names.References(named).Where(o => includeDeclaration || !o.IsDeclaration).Select(o => Location(analysis.Syntax, o.Span));
            if (named.NameScopeId == analysis.Document.Root?.NameScopeId)
                locations = locations.Concat(csharp.FindName(analysis, named.Name, workspace, includeDeclaration, token).Select(Location));
            return locations.ToArray();
        }
        if (resources.GetReferences(analysis).FirstOrDefault(r => r.Span.Contains(position)) is { } selected)
            return workspace.SelectMany(a => resources.GetReferences(a).Where(r => r.ResourceUri == selected.ResourceUri).Select(r => Location(a.Syntax, r.Span))).ToArray();
        var symbol = analysis.Document.Symbols.Where(s => s.Span.Contains(position)).OrderBy(s => s.Span.Length).FirstOrDefault()?.Symbol;
        if (symbol == null) return Array.Empty<object>();
        return workspace.SelectMany(a => a.Document.Symbols
            .Where(s => SymbolEqualityComparer.Default.Equals(symbol.OriginalDefinition, s.Symbol.OriginalDefinition))
            .Select(s => s.Span).Distinct().Select(span => Location(a.Syntax, span)))
            .Concat(csharp.Find(symbol, includeDeclaration, token).Select(Location)).ToArray();
    }
    private static object[] WorkspaceSymbols(JsonElement parameters, ImmutableArray<XamlAnalysis> workspace, CancellationToken token)
    {
        var query = parameters.TryGetProperty("query", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        var result = new List<object>();
        foreach (var analysis in workspace)
        {
            token.ThrowIfCancellationRequested();
            var root = analysis.Syntax.Root;
            if (root == null) continue;
            var name = analysis.Document.ClassName ?? root.Name;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(new { name, kind = 5, location = Location(analysis.Syntax, root.NameSpan), containerName = Path.GetFileName(analysis.Syntax.Path) });
            foreach (var obj in BoundDocumentTraversal.Objects(analysis.Document).Where(o => o.Name != null && o.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                result.Add(new { name = obj.Name!, kind = 13, location = Location(analysis.Syntax, obj.Syntax.NameSpan), containerName = name });
                if (result.Count >= 256) return result.ToArray();
            }
            if (result.Count >= 256) break;
        }
        return result.ToArray();
    }
    private static object Location(XamlDefinition location) => new
    {
        uri = LspConversions.UriForPath(location.Path),
        range = new LspRange(new(location.Start.Line, location.Start.Character), new(location.End.Line, location.End.Character))
    };
    private static object Location(XamlSyntaxTree syntax, TextSpan span) => new { uri = LspConversions.UriForPath(syntax.Path), range = LspConversions.Range(syntax, span) };
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
