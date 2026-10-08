using System.Text.Json;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class RuntimeWorkbench
{
    private IJSObjectReference? _preferencesModule;
    private bool _preferencesLoaded, _preferenceSaveScheduled, _preferenceSavePending;
    private string? _lastPreferences, _failedPreferences;
    private string PreferenceKey => "runtime-ui:" + Panel;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _preferencesModule = await JavaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "studio.js");
                var saved = await _preferencesModule.InvokeAsync<SavedRuntime?>("loadStudioState", PreferenceKey);
                if (saved is { Version: 1 })
                {
                    _treeFilter = saved.TreeFilter; _treeMode = saved.TreeMode; _propertyFilter = saved.PropertyFilter;
                    _propertyJson = saved.PropertyJson; _key = saved.Key; _text = saved.Text; _button = saved.Button;
                    _modifiers = saved.Modifiers; _x = saved.X; _y = saved.Y; _wheelX = saved.WheelX; _wheelY = saved.WheelY;
                    _objectPath = saved.ObjectPath; _methodArguments = saved.MethodArguments; _providerArguments = saved.ProviderArguments;
                    _event = saved.Event; _toolName = saved.ToolName; _toolArguments = saved.ToolArguments; _toolFilter = saved.ToolFilter;
                }
                _preferencesLoaded = true; StateHasChanged();
            }
            catch (JSException error) { _error = error.Message; StateHasChanged(); }
        }
        else if (_preferencesLoaded && !_disposed)
        {
            _preferenceSavePending = true;
            if (_preferenceSaveScheduled) return;
            _preferenceSaveScheduled = true; _ = SavePreferencesLaterAsync();
        }
    }
    private async Task SavePreferencesLaterAsync()
    {
        try
        {
            do
            {
                await Task.Delay(200, _lifetime.Token);
                _preferenceSavePending = false;
                await InvokeAsync(SavePreferencesAsync);
            } while (_preferenceSavePending && !_disposed);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _preferenceSaveScheduled = false; }
    }
    private async Task SavePreferencesAsync()
    {
        if (!_preferencesLoaded || _preferencesModule == null) return;
        // Preserve user input, never runtime handles, observed revisions or active input state.
        var saved = new SavedRuntime(1, _treeFilter, _treeMode, _propertyFilter, _propertyJson, _key, _text, _button,
            _modifiers, _x, _y, _wheelX, _wheelY, _objectPath, _methodArguments, _providerArguments, _event, _toolName, _toolArguments, _toolFilter);
        var text = JsonSerializer.Serialize(saved);
        if (text == _lastPreferences || text == _failedPreferences) return;
        try { await _preferencesModule.InvokeVoidAsync("saveStudioState", PreferenceKey, saved); _lastPreferences = text; _failedPreferences = null; }
        catch (JSException error) { _failedPreferences = text; _error = error.Message; if (!_disposed) StateHasChanged(); }
    }
    private sealed record SavedRuntime(int Version, string TreeFilter, string TreeMode, string PropertyFilter, string PropertyJson,
        string Key, string Text, string Button, string Modifiers, double? X, double? Y, double WheelX, double WheelY,
        string ObjectPath, string MethodArguments, string ProviderArguments, string Event, string ToolName, string ToolArguments, string ToolFilter);
}
