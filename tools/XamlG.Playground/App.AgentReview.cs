using XamlG.Playground.Components;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
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
        if (request.Path == CompilerSettingsPath) await ShowPaneAsync("compiler");
        else await RevealDocumentAsync(request.Path, new(start, end - start));
        StateHasChanged();
    }
}
