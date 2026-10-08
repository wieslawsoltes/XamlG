using Microsoft.JSInterop;
using XamlG.Agents;

namespace XamlG.Playground;

public partial class App
{
    private bool _savedAgentsLoaded;
    private Task? _initialStateRestore;
    private bool _shellStateLoaded;
    private string? _lastShellState, _failedShellState;
    private int? _previewWidth, _previewHeight;
    private string PreviewViewportStyle => _previewWidth is { } width && _previewHeight is { } height
        ? $"right:auto;bottom:auto;width:{width}px;height:{height}px;" : "";
    private Task RestoreInitialStateAsync() => _initialStateRestore ??= RestoreInitialStateCoreAsync();
    private async Task RestoreInitialStateCoreAsync()
    {
        _module ??= await JavaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "studio.js");
        await RestoreDraftCoreAsync(startup: true);
        await SaveDraftAsync();
        var shell = await _module.InvokeAsync<SavedShell?>("loadStudioState", "shell");
        if (shell is { Version: 1 })
        {
            _activeDocumentPath = shell.ActiveDocument; _editorTab = shell.EditorTab; _inspectorTab = shell.InspectorTab;
            _propertyName = shell.PropertyName; _propertyValue = shell.PropertyValue;
            _automationProfile = shell.AutomationProfile; _capabilityFilter = shell.CapabilityFilter; _capabilityScope = shell.CapabilityScope;
            _designMode = shell.DesignMode; _isolationVisible = shell.Isolated;
            if (shell.PreviewWidth is >= 128 and <= 4096 && shell.PreviewHeight is >= 128 and <= 4096)
            { _previewWidth = shell.PreviewWidth; _previewHeight = shell.PreviewHeight; }
        }
        _shellStateLoaded = true;
    }
    private async Task SaveShellStateAsync()
    {
        if (!_shellStateLoaded || _module == null || _disposed) return;
        var state = new SavedShell(1, _activeDocumentPath, _editorTab, _inspectorTab, _propertyName, _propertyValue,
            _automationProfile, _capabilityFilter, _capabilityScope, _designMode, _isolationVisible, _previewWidth, _previewHeight);
        var text = System.Text.Json.JsonSerializer.Serialize(state);
        if (_lastShellState == text || _failedShellState == text) return;
        try { await _module.InvokeVoidAsync("saveStudioState", "shell", state); _lastShellState = text; _failedShellState = null; }
        catch (JSException) { _failedShellState = text; throw; }
    }
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
        _browserAgents.IsStateLoaded = true;
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
        RevokeAutomation();
        if (_module != null) await _module.InvokeVoidAsync("disconnectAutomation");
        _connectionStatus = "Disconnected";
        _companionToken = "";
        if (_module != null) await _module.InvokeVoidAsync("forgetStudioState", "companion-connection");
    }
    private sealed record SavedCompanion(string Address, string Token, bool Connected);
    private sealed record SavedShell(int Version, string ActiveDocument, string EditorTab, string InspectorTab, string PropertyName, string PropertyValue,
        XamlG.Automation.PermissionProfile AutomationProfile, string CapabilityFilter, string CapabilityScope, bool DesignMode, bool Isolated, int? PreviewWidth, int? PreviewHeight);
}
