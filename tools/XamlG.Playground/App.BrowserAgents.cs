using System.Text.Json;
using XamlG.Automation;

namespace XamlG.Playground;

public partial class App
{
    private BrowserAgentRuntime _browserAgents = default!;

    private async ValueTask<JsonElement> InvokeBrowserAgentAsync(string name, JsonElement arguments, AutomationCallContext context)
    {
        // The reusable harness already reviewed this invocation under its own lease.
        // This owner adapter does not enable or borrow authority from external MCP.
        var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == name) ?? throw new ArgumentException("Unknown IDE operation.");
        if (name == "xamlg_wait") return await _automation.CallLocalAsync(name, arguments, context);
        await _automationGate.WaitAsync(context.CancellationToken);
        try
        {
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "Wait for the current IDE operation.");
            var needsSource = tool.Scope != AutomationScope.Runtime || name == "xamlg_runtime_run";
            if (needsSource) await CaptureEditorsAsync();
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!_ready || _busy || _disposed) throw new AutomationException("unavailable", "The IDE changed while capturing source.");
            var result = await _automation.CallLocalAsync(name, arguments, context with { Caller = "Coding agent", PrincipalId = "browser-agent" });
            if (needsSource && tool.Effect != AutomationEffect.Read) await SaveDraftAsync();
            return result;
        }
        finally { _automationGate.Release(); if (!_disposed) StateHasChanged(); }
    }

    private sealed class BrowserAgentHost(App owner) : IAutomationHost
    {
        public IReadOnlyList<AutomationTool> Tools => owner._automation.Tools;
        public IReadOnlyList<AutomationResource> Resources => [];
        public IReadOnlyList<AutomationPrompt> Prompts => owner._automation.Prompts;
        public ValueTask<JsonElement> CallAsync(string name, JsonElement arguments, AutomationCallContext context) => owner.InvokeBrowserAgentAsync(name, arguments, context);
        public ValueTask<string> ReadResourceAsync(string uri, AutomationCallContext context) => throw new AutomationException("permission_denied", "Coding agents read project data through permission-checked tools.");
    }
}
