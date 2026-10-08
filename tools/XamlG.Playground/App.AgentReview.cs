using XamlG.Playground.Components;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    private (string Path, TextSpan Span, long Revision)? _pendingAgentReveal;
    private async Task NavigateAgentSourceAsync(AgentWorkbench.AgentSourceNavigation request)
    {
        if (!_ready || _busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
        await CaptureEditorsAsync();
        if (SourceRevision != request.ExpectedRevision) throw new InvalidOperationException("Source changed. Refresh the review before navigating.");
        if (!_workspaceEdits.Current.Documents.TryGetValue(request.Path, out var text)) throw new InvalidOperationException("This document no longer exists.");
        if (request.Line < 1) throw new ArgumentOutOfRangeException(nameof(request.Line));
        var start = 0;
        for (var line = 1; line < request.Line; line++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0) throw new InvalidOperationException("The reviewed line is unavailable.");
            start = next + 1;
        }
        var end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
        _pendingAgentReveal = (request.Path, new(start, end - start), request.ExpectedRevision);
        if (request.Path is "View.axaml" or "Code.cs")
        { _editorTab = request.Path == "Code.cs" ? "code" : "xaml"; await ShowPaneAsync("source"); }
        else
        {
            _inspectorTab = request.Path == CompilerSettingsPath ? "Compiler" : IsCSharpPath(request.Path) ? "C# files" : "Resources";
            await ShowPaneAsync("inspector");
        }
        StateHasChanged();
    }
    private async Task RevealAgentSourceAsync()
    {
        if (_pendingAgentReveal is not { } pending) return;
        if (SourceRevision != pending.Revision) { _pendingAgentReveal = null; return; }
        switch (pending.Path)
        {
            case "View.axaml" when _xamlEditor != null: _pendingAgentReveal = null; await _xamlEditor.RevealAsync(pending.Span); break;
            case "Code.cs" when _codeEditor != null: _pendingAgentReveal = null; await _codeEditor.RevealAsync(pending.Span); break;
            case CompilerSettingsPath: _pendingAgentReveal = null; break;
            default:
                if (IsCSharpPath(pending.Path) && _projectCodeEditor != null) { _pendingAgentReveal = null; _projectCodeEditor.Reveal(pending.Path, pending.Span); }
                else if (!IsCSharpPath(pending.Path) && _resourceEditor != null) { _pendingAgentReveal = null; _resourceEditor.Reveal(pending.Path, pending.Span); }
                break;
        }
    }
}
