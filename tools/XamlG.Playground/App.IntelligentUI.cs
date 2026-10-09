using System.Text.Json;
using Microsoft.AspNetCore.Components;
using XamlG.Automation;
using XamlG.IntelligentUI;

namespace XamlG.Playground;

public partial class App
{
    private readonly UiSessionStore _intelligentUi = new();
    private UiAutomation? _intelligentUiAutomation;
    private UiDataAutomation? _intelligentUiData;
    [Inject] public IntelligentUiWorkspaceContext IntelligentUiWorkspace { get; set; } = default!;
    private void InitializeIntelligentUi()
    {
        _intelligentUiAutomation = new(_automation, _intelligentUi);
        _intelligentUiData = new(_automation, _intelligentUi, captureAuthorizedResult: tool =>
            tool.Scope is AutomationScope.Source or AutomationScope.Compiler or AutomationScope.Project);
        var proposals = new UiCSharpProposals(_intelligentUi); proposals.Register(_automation);
        UiNativeAppResource.Register(_automation, _intelligentUi, new Uri(Navigation.BaseUri));
        IntelligentUiWorkspace.Attach(_intelligentUi, proposals, () => _browserAgents.WorkspaceIdentity,
            WithIntelligentUiWorkspaceAsync, () => _browserAgents.WorkspaceLifetime);
    }
    private void DisposeIntelligentUi()
    {
        IntelligentUiWorkspace.Detach(_intelligentUi);
        _intelligentUiData?.Dispose(); _intelligentUiAutomation?.Dispose(); _intelligentUi.Clear();
    }
    private async Task WithIntelligentUiWorkspaceAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        await _automationGate.WaitAsync(token);
        try
        {
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "The Studio workspace is busy or not ready.");
            await CaptureEditorsAsync(); token.ThrowIfCancellationRequested();
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "The workspace changed while capturing editor state.");
            // Save the project identity before storing a UI archive, including new untouched projects.
            await SaveDraftAsync(); token.ThrowIfCancellationRequested();
            await operation(token);
        }
        finally { _automationGate.Release(); if (!_disposed) StateHasChanged(); }
    }
    // Owner-only callback after explicit local action review. Not exposed to agents or JS.
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
