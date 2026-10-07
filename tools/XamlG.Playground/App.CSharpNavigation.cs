using Microsoft.AspNetCore.Components;
using XamlG.Playground.Components;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    private string? _generatedPath;
    private CodeEditor? _generatedEditor;
    private (string Path, TextSpan Span)? _pendingGeneratedReveal;
    private string[] GeneratedPaths => _result?.Compilation.SyntaxTrees.Where(t => !_result.SourcePaths.Contains(t.FilePath))
        .Select(t => t.FilePath).Order(StringComparer.Ordinal).ToArray() ?? [];
    private string SelectedGeneratedPath => GeneratedPaths.Contains(_generatedPath, StringComparer.Ordinal) ? _generatedPath! :
        _result?.Analysis.Output.HintName ?? "";
    private string GeneratedText => _result?.Compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == SelectedGeneratedPath)?.ToString()
        ?? "// Generated C# appears here after compilation.\n";
    private void GeneratedPathChanged(ChangeEventArgs args) { _generatedPath = args.Value?.ToString(); _pendingGeneratedReveal = null; }
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
        if (request.Path == "Code.cs")
        {
            _editorTab = "code"; await ShowPaneAsync("source"); StateHasChanged();
            if (_codeEditor != null) await _codeEditor.RevealAsync(span);
        }
        else if (Compiler.CodeFiles.Snapshot.ContainsKey(request.Path))
        {
            _inspectorTab = "C# files"; _projectCodeEditor?.Reveal(request.Path, span);
            await ShowPaneAsync("inspector"); StateHasChanged();
        }
        else
        {
            _generatedPath = request.Path; _inspectorTab = "C# output";
            _pendingGeneratedReveal = (request.Path, span);
            await ShowPaneAsync("inspector"); StateHasChanged();
        }
    }
    private async Task RevealGeneratedAsync()
    {
        if (_pendingGeneratedReveal is { } pending && _generatedEditor?.DocumentPath == pending.Path)
        { _pendingGeneratedReveal = null; await _generatedEditor.RevealAsync(pending.Span); }
    }
}
