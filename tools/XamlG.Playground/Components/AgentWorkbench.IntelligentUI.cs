using System.Text.Json;
using Microsoft.AspNetCore.Components;
using XamlG.Automation;
using XamlG.IntelligentUI;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    [Parameter] public UiSessionStore? UiStore { get; set; }
    [Parameter] public bool UiReady { get; set; }
    [Parameter] public IReadOnlyList<AutomationTool> UiTools { get; set; } = [];
    [Parameter] public Func<UiActionCall, CancellationToken, Task<JsonElement>>? UiToolRequested { get; set; }
    private UiPresentation? _uiDemo;
    private void ShowIntelligentUiDemo()
    {
        if (UiStore == null || !UiReady) return;
        if (_uiDemo == null || UiStore.ReadLocal(_uiDemo.Id)?.SessionId != _uiDemo.SessionId)
            _uiDemo = UiPresentation.From(UiStore.Publish(UiExamples.Pricing("demo-" + Guid.NewGuid().ToString("N")), "studio-user"));
    }
    private async Task UseUiMessageAsync(string text)
    {
        if (text.Length > 100000) throw new ArgumentException("The message is too long.");
        _draft = text;
        if (Selected != null) await SelectSectionAsync("Conversation");
        // This never invokes CommandAsync, queues a turn, or starts provider inference.
        StateHasChanged();
    }
}
