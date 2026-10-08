using Microsoft.JSInterop;
using XamlG.Agents;

namespace XamlG.Playground;

public partial class App
{
    private bool _savedAgentsLoaded;
    private async Task InitializeSavedAgentsAsync()
    {
        if (_savedAgentsLoaded || _module == null) return;
        var saved = await _module.InvokeAsync<AgentSessionSnapshot?>("loadStudioState", "browser-agents");
        if (saved != null) _browserAgents.Session.RestoreSession(saved, _browserAgents.WorkspaceLifetime);
        _browserAgents.Session.Harness.PersistSession = async (snapshot, cancellationToken) =>
        {
            // Flush edited source before the matching agent continuation. A restart may
            // retain an uncertain operation, but cannot claim an unsaved edit completed.
            await InvokeAsync(async () =>
            {
                if (_disposed || _module == null) return;
                await SaveDraftAsync(onlyIfChanged: true);
                await _module.InvokeVoidAsync("saveStudioState", cancellationToken, "browser-agents", snapshot);
            });
        };
        _savedAgentsLoaded = true;
    }

    private async Task RestoreCompanionConnectionAsync()
    {
        if (_module == null) return;
        var saved = await _module.InvokeAsync<SavedCompanion?>("loadStudioState", "companion-connection");
        if (saved == null) return;
        _companionUrl = saved.Address; _companionToken = saved.Token;
        // Restoring a connection never restores an external MCP permission lease.
        if (saved.Connected) await ConnectAutomationAsync();
    }
    private async Task ForgetCompanionConnectionAsync()
    {
        await DisconnectAutomationAsync();
        _companionToken = "";
        if (_module != null) await _module.InvokeVoidAsync("forgetStudioState", "companion-connection");
    }
    private sealed record SavedCompanion(string Address, string Token, bool Connected);
}
