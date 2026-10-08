using System.Text.Json;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly JsonSerializerOptions SavedJson = new(ViewJson) { IncludeFields = true };
    private Dictionary<string, SavedConnection> _savedConnections = new(StringComparer.Ordinal);
    private string? _credentialProfileKey, _lastSavedUi, _failedSavedUi;
    private bool _preferencesLoaded, _saveScheduled;
    private string? _storageError;

    private void RememberConnection()
    {
        if (_credentialProfileKey != null)
            _savedConnections[_credentialProfileKey] = new(_apiKey, _relayUrl, _relayToken, _acceptBrowserExposure);
    }
    private void SelectConnectionProfile()
    {
        var key = _connectionMode + ":" + _provider;
        if (_credentialProfileKey == key) return;
        RememberConnection(); _credentialProfileKey = key;
        var saved = _savedConnections.GetValueOrDefault(key) ?? new("", "http://127.0.0.1:4893", "", false);
        _apiKey = saved.ApiKey; _relayUrl = saved.RelayUrl; _relayToken = saved.RelayToken; _acceptBrowserExposure = saved.AcceptBrowserExposure;
    }
    private async Task ForgetConnectionAsync()
    {
        BrowserRuntime?.ClearCredentials();
        _apiKey = ""; _relayToken = ""; _acceptBrowserExposure = false;
        if (_credentialProfileKey != null) _savedConnections.Remove(_credentialProfileKey);
        if (_module == null) return;
        // Remove the retained previous version too; forgetting must not leave a recoverable key.
        await _module.InvokeVoidAsync("forgetStudioState", "agent-ui");
        _lastSavedUi = null; await SaveUiStateAsync();
    }
    private async Task LoadUiStateAsync()
    {
        try
        {
            var value = await _module!.InvokeAsync<JsonElement?>("loadStudioState", "agent-ui");
            if (value is { ValueKind: JsonValueKind.Object } json && json.Deserialize<SavedWorkbench>(SavedJson) is { Version: 1 } saved)
            {
                _connectionMode = saved.Mode is "direct" or "relay" or "companion" ? saved.Mode : "direct";
                _provider = saved.Provider; _model = saved.Model; _name = saved.Name;
                _section = Sections.Contains(saved.Section) ? saved.Section : "Conversation";
                _savedConnections = saved.Connections;
                foreach (var (key, preference) in saved.Preferences.Where(pair => pair.Value.Numeric.IsValid())) _taskPreferences[key] = preference;
                foreach (var (key, draft) in saved.Drafts) ComposerDrafts[key] = draft;
                foreach (var (key, id) in saved.Selected) LastSelectedTasks[key] = id;
                foreach (var (key, backend) in saved.TaskConnections) TaskConnections[key] = backend;
                foreach (var (key, editor) in saved.QueueEditors) _queueEditors[key] = editor;
                _toolFilter = saved.ToolFilter; _toolScope = saved.ToolScope;
                _agentActivityFilter = saved.ActivityFilter; _activityCurrentTask = saved.ActivityCurrentTask;
                _rememberNewAccount = saved.RememberNewAccount;
                foreach (var (id, review) in saved.Reviews ?? []) ReviewStates[id] = new()
                {
                    LatestRun = review.LatestRun, Unified = review.Unified, Path = review.Path,
                    Feedback = review.Feedback, FeedbackPath = review.FeedbackPath,
                    LineLimit = Math.Clamp(review.LineLimit, 1, 10000)
                };
            }
            SelectConnectionProfile(); _preferencesLoaded = true;
        }
        catch (Exception error) when (error is JSException or JsonException) { _storageError = error.Message; }
    }
    private void ScheduleUiSave()
    {
        if (!_preferencesLoaded || _saveScheduled || _disposed) return;
        _saveScheduled = true; _ = SaveLaterAsync();
    }
    private async Task SaveLaterAsync()
    {
        try { await Task.Delay(200, _lifetime.Token); await InvokeAsync(SaveUiStateAsync); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _saveScheduled = false; }
    }
    private async Task SaveUiStateAsync()
    {
        if (!_preferencesLoaded || _module == null) return;
        RememberConnection();
        var saved = new SavedWorkbench(1, _connectionMode, _provider, _model, _name, _section, _savedConnections,
            _taskPreferences, ComposerDrafts, LastSelectedTasks, TaskConnections, _queueEditors,
            _toolFilter, _toolScope, _agentActivityFilter, _activityCurrentTask, _rememberNewAccount,
            ReviewStates.ToDictionary(pair => pair.Key, pair => new SavedReview(pair.Value.LatestRun, pair.Value.Unified,
                pair.Value.Path, pair.Value.Feedback, pair.Value.FeedbackPath, pair.Value.LineLimit)));
        var text = JsonSerializer.Serialize(saved, SavedJson);
        if (text == _lastSavedUi || text == _failedSavedUi) return;
        try
        {
            await _module.InvokeVoidAsync("saveStudioState", "agent-ui", JsonSerializer.Deserialize<JsonElement>(text));
            _lastSavedUi = text; _failedSavedUi = null; _storageError = null;
        }
        catch (JSException error) { _failedSavedUi = text; _storageError = error.Message; StateHasChanged(); }
    }
    private sealed record SavedConnection(string ApiKey, string RelayUrl, string RelayToken, bool AcceptBrowserExposure);
    private sealed record SavedReview(bool LatestRun, bool Unified, string Path, string Feedback, string FeedbackPath, int LineLimit);
    private sealed record SavedWorkbench(int Version, string Mode, string Provider, string Model, string Name, string Section,
        Dictionary<string, SavedConnection> Connections, Dictionary<string, TaskPreferences> Preferences,
        Dictionary<string, string> Drafts, Dictionary<string, string> Selected, Dictionary<string, string> TaskConnections,
        Dictionary<string, QueueEditor> QueueEditors, string ToolFilter, string ToolScope, string ActivityFilter, bool ActivityCurrentTask, bool RememberNewAccount,
        Dictionary<string, SavedReview>? Reviews = null);
}
