using System.Text.Json;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly Dictionary<string, TaskPreferences> _taskPreferences = new(StringComparer.Ordinal);
    private NumericPreferences _numericDefaults = new();
    private TaskPreferences Preferences => _taskPreferences.TryGetValue(_selectedId, out var preferences)
        ? preferences : _taskPreferences[_selectedId] = new() { Numeric = _numericDefaults with { }, Profile = _state.Constraints.AllowedProfiles.FirstOrDefault() ?? "ask" };
    private Dictionary<string, string> _scopes => Preferences.Scopes;
    private string _profile { get => Preferences.Profile; set => Preferences.Profile = value; }
    private string _toolRules { get => Preferences.ToolRules; set => Preferences.ToolRules = value; }
    private bool _neverAsk { get => Preferences.NeverAsk; set => Preferences.NeverAsk = value; }
    private bool _autoCompact { get => Preferences.AutoCompact; set => Preferences.AutoCompact = value; }
    private int _requests { get => Preferences.Numeric.Requests; set => Preferences.Numeric.Requests = value; }
    private int _tools { get => Preferences.Numeric.Tools; set => Preferences.Numeric.Tools = value; }
    private int _outputTokens { get => Preferences.Numeric.OutputTokens; set => Preferences.Numeric.OutputTokens = value; }
    private int _contextBytes { get => Preferences.Numeric.ContextBytes; set => Preferences.Numeric.ContextBytes = value; }
    private int _toolResultBytes { get => Preferences.Numeric.ToolResultBytes; set => Preferences.Numeric.ToolResultBytes = value; }
    private int _leaseMinutes { get => Preferences.Numeric.LeaseMinutes; set => Preferences.Numeric.LeaseMinutes = value; }
    private int _retries { get => Preferences.Numeric.Retries; set => Preferences.Numeric.Retries = value; }
    private int _timeoutMinutes { get => Preferences.Numeric.TimeoutMinutes; set => Preferences.Numeric.TimeoutMinutes = value; }
    private long _taskTokens { get => Preferences.Numeric.TaskTokens; set => Preferences.Numeric.TaskTokens = value; }

    private async Task LoadNumericPreferencesAsync()
    {
        try
        {
            var saved = await _module!.InvokeAsync<NumericPreferences?>("loadAgentNumericPreferences");
            if (saved != null && saved.IsValid()) _numericDefaults = saved;
        }
        catch (Exception error) when (error is JSException or JsonException) { }
    }

    private async Task SaveNumericPreferencesAsync()
    {
        if (!Preferences.Numeric.IsValid()) { _error = "Enter valid numeric limits before saving defaults."; return; }
        try
        {
            _numericDefaults = Preferences.Numeric with { };
            await _module!.InvokeVoidAsync("saveAgentNumericPreferences", _numericDefaults);
        }
        catch (JSException error) { _error = error.Message; }
    }

    private void SetPreset(bool large) => Preferences.Numeric = new() { TaskTokens = large ? 20_000_000 : 4_000_000 };
    private void ResetPermissions()
    { _profile = "ask"; _scopes.Clear(); _toolRules = "{}"; _neverAsk = false; }

    private sealed class TaskPreferences
    {
        public NumericPreferences Numeric = new();
        public Dictionary<string, string> Scopes { get; set; } = new(StringComparer.Ordinal);
        public string Profile = "ask", ToolRules = "{}";
        public bool NeverAsk, AutoCompact = true, FullToolCatalog, ReviewBeforeSend, ContinueQueue = true;
    }

    public sealed record NumericPreferences
    {
        public int Requests { get; set; } = 128;
        public int Tools { get; set; } = 1024;
        public int OutputTokens { get; set; } = 32768;
        public long TaskTokens { get; set; } = 4_000_000;
        public int ContextBytes { get; set; } = 6_000_000;
        public int ToolResultBytes { get; set; } = 524288;
        public int Retries { get; set; } = 3;
        public int TimeoutMinutes { get; set; } = 10;
        public int LeaseMinutes { get; set; } = 10;
        public int AutomaticInputTokens { get; set; } = 64000;
        public int ModelContextWindowTokens { get; set; }
        public int RecentCompleteTurns { get; set; } = 2;
        public int CheckpointOutputTokens { get; set; } = 2048;
        public bool IsValid() => Requests is >= 1 and <= 10000 && Tools is >= 1 and <= 10000 &&
            OutputTokens is >= 1 and <= 1_000_000 && TaskTokens is >= 1 and <= 100_000_000 &&
            ContextBytes is >= 1024 and <= 16_000_000 && ToolResultBytes is >= 1024 and <= 8_388_608 &&
            Retries is >= 0 and <= 10 && TimeoutMinutes is >= 1 and <= 30 && LeaseMinutes is >= 1 and <= 60 &&
            AutomaticInputTokens is >= 0 and <= 2_000_000 && ModelContextWindowTokens is >= 0 and <= 4_000_000 &&
            RecentCompleteTurns is >= 0 and <= 16 && CheckpointOutputTokens is >= 256 and <= 8192;
    }
}
