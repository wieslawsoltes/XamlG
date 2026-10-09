using System.Text.Json;
using XamlG.Automation;
using XamlG.IntelligentUI;

namespace XamlG.Playground;

public partial class App
{
    private readonly UiSessionStore _intelligentUi = new();
    private UiAutomation? _intelligentUiAutomation;
    private void InitializeIntelligentUi() => _intelligentUiAutomation = new(_automation, _intelligentUi);
    private void DisposeIntelligentUi() { _intelligentUiAutomation?.Dispose(); _intelligentUi.Clear(); }

    // This method is not JSInvokable and is never exposed to agents. The card invokes it
    // only after an explicit local user review; normal schema/source-revision checks remain.
    private async Task<JsonElement> ExecuteIntelligentUiToolAsync(UiActionCall call, CancellationToken cancellationToken)
    {
        await _automationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "Wait for the current IDE operation.");
            await CaptureEditorsAsync(); cancellationToken.ThrowIfCancellationRequested();
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "The IDE operation was superseded.");
            var intent = _intelligentUi.PrepareActionLocal(call);
            if (intent.Kind != "tool" || intent.Tool == null) throw new AutomationException("invalid_action", "This is not a tool action.");
            // Event-driven waits intentionally do not take the mutation gate in the agent
            // transport. They are not meaningful as a blocking local button operation.
            if (intent.Tool == "xamlg_wait") throw new AutomationException("invalid_action", "Use the agent's wait tool rather than a blocking UI button.");
            var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == intent.Tool) ?? throw new AutomationException("unknown_tool", "The tool is no longer available.");
            var before = SourceRevision;
            var result = await _automation.CallLocalAsync(tool.Name, intent.Arguments ?? AutomationJson.Element(new { }), new("Intelligent UI user", cancellationToken, "studio-user"));
            if (SourceRevision != before) await SaveDraftAsync();
            return result;
        }
        finally { _automationGate.Release(); if (!_disposed) StateHasChanged(); }
    }
}
