using Microsoft.AspNetCore.Components;
using XamlG.Playground.Components;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    private string[] GeneratedPaths => _result?.Compilation.SyntaxTrees.Where(t => !_result.SourcePaths.Contains(t.FilePath))
        .Select(t => t.FilePath).Order(StringComparer.Ordinal).ToArray() ?? [];
    private Task OpenGeneratedDocumentAsync(string hintName)
    {
        var path = GeneratedPaths.FirstOrDefault(path => path == hintName || path.EndsWith("/" + hintName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Compile this document before opening its generated code.");
        return OpenDocumentAsync(path, generated: true);
    }
    private async Task NavigateCSharpAsync(CSharpNavigationRequest request)
    {
        if (!_ready || _busy) return;
        await CaptureEditorsAsync();
        var text = CSharpLanguage().Tree(request.Path).GetText();
        if (request.StartLine < 0 || request.EndLine < request.StartLine || request.EndLine >= text.Lines.Count) return;
        var startLine = text.Lines[request.StartLine]; var endLine = text.Lines[request.EndLine];
        if (request.StartColumn < 0 || request.StartColumn > startLine.Span.Length || request.EndColumn < 0 || request.EndColumn > endLine.Span.Length) return;
        var start = startLine.Start + request.StartColumn; var end = endLine.Start + request.EndColumn;
        if (end < start) return;
        var span = new TextSpan(start, end - start);
        await RevealDocumentAsync(request.Path, span, generated: !WorkspaceTexts().ContainsKey(request.Path));
    }
}
