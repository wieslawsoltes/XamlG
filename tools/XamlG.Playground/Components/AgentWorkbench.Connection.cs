using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Agents;
using XamlG.Automation;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    [Parameter] public BrowserAgentRuntime? BrowserRuntime { get; set; }
    [Parameter] public EventCallback AccessRequested { get; set; }
    private string _connectionMode = "direct", _apiKey = "", _relayUrl = "http://127.0.0.1:4893", _relayToken = "";
    private bool _acceptBrowserExposure;
    private bool IsDirect => _connectionMode == "direct";
    private bool IsBrowser => _connectionMode != "companion";
    private string BackendId => IsBrowser ? "browser" : "companion";
    private static readonly JsonSerializerOptions ViewJson = new(AutomationJson.Options)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip, RespectRequiredConstructorParameters = false };

    public async Task ClosePanelAsync()
    {
        if (_disposed) return;
        await SaveUiStateAsync();
        if (IsBrowser) BrowserRuntime?.ClearCredentials();
        else if (_connected) await RequestAsync<JsonElement>("stop", new { });
        _fullAccessAcknowledged = false; _runReview = null; _restoreReview = null; _handoff = null;
    }

    private void ClearDirectCredentials()
    { _apiKey = ""; _relayToken = ""; _acceptBrowserExposure = false; BrowserRuntime?.ClearCredentials(); }

    private async Task ConnectionChangedAsync(ChangeEventArgs args)
    {
        var mode = args.Value?.ToString();
        if (mode is not ("direct" or "relay" or "companion") || mode == _connectionMode || AnyRunning || _modelsBusy || _runReview != null) return;
        _connectionVersion++;
        RememberConnection(); BrowserRuntime?.ClearCredentials(); _rememberedConnectionMode = _connectionMode = mode; _toolCatalog = []; _agentActivity = []; _state = new(); _selectedId = "";
        _provider = mode != "companion" ? "openai" : ""; _models = []; _model = ""; _liveText = "";
        SelectConnectionProfile();
        _runReview = null; _restoreReview = null; _handoff = null; _error = null;
        await RefreshAfterCommandAsync();
    }

    private async ValueTask<T> RequestAsync<T>(string action, object arguments)
    {
        if (!IsBrowser)
        {
            try { return await _module!.InvokeAsync<T>("agentRequest", action, arguments); }
            catch (JSException error) { throw new JSException(error.Message.Split('\n')[0]); }
        }
        try
        {
            var runtime = BrowserRuntime ?? throw new InvalidOperationException("The browser agent session is not ready.");
            if (action is "run" or "compact" or "models" or "model_choices" || action == "send" && !runtime.Session.IsRunning)
            {
                if (action is "run" or "compact" or "send" && Selected?.ProviderId != _provider)
                    throw new InvalidOperationException("Select the task's provider before entering its API key.");
                if (IsDirect) runtime.Configure(_provider, _apiKey, _acceptBrowserExposure);
                else runtime.ConfigureRelay(_provider, _relayUrl, _relayToken);
            }
            var result = await runtime.ExecuteAsync(action, AutomationJson.Element(arguments), _lifetime.Token);
            return result.Deserialize<T>(ViewJson)!;
        }
        catch (Exception error) when (error is not (JSException or OutOfMemoryException))
        { throw new JSException(error is OperationCanceledException ? "The browser agent operation was cancelled." : error.Message); }
    }

    private void BrowserEventPublished(AgentEvent item)
    {
        if (_disposed || !IsBrowser) return;
        _ = InvokeAsync(async () =>
        {
            if (!_disposed && IsBrowser)
                await ProcessStreamAsync(new EventView { Sequence = item.Sequence, TaskId = item.TaskId, Kind = item.Kind, Text = item.Text, ToolName = item.ToolName, ToolCallId = item.ToolCallId });
        });
    }
}
