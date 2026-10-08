using System.Text.Json;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private static readonly string[] Sections = ["Conversation", "Connection", "Tasks", "Plan", "Changes", "Queue", "Permissions", "Tools", "Activity"];
    private string _section = "Connection", _toolFilter = "", _toolScope = "", _ruleTool = "", _ruleDecision = "ask";
    private string _agentActivityFilter = "";
    private string? _renderedSection;
    private Microsoft.AspNetCore.Components.ElementReference _contentElement;
    private bool _activityCurrentTask = true, _modelsBusy;
    private string? _deleteTaskId;
    private ToolView[] _toolCatalog = [];
    private ActivityView[] _agentActivity = [];
    private static readonly Dictionary<string, string> TaskConnections = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastSelectedTasks = new(StringComparer.Ordinal);
    private static string? _rememberedConnectionMode;
    private IEnumerable<ToolView> VisibleTools => _toolCatalog.Where(tool => (_toolScope.Length == 0 || tool.Scope == _toolScope) &&
        (_toolFilter.Length == 0 || (tool.Name + " " + tool.Description).Contains(_toolFilter, StringComparison.OrdinalIgnoreCase)));
    private IEnumerable<ActivityView> VisibleActivity => _agentActivity.Where(item => (!_activityCurrentTask || item.TaskId == _selectedId) &&
        (_agentActivityFilter.Length == 0 || (item.Kind + " " + item.Text).Contains(_agentActivityFilter, StringComparison.OrdinalIgnoreCase)));
    private Dictionary<string, string> ToolRuleValues => JsonSerializer.Deserialize<Dictionary<string, string>>(_toolRules) ?? new();
    private bool RetiredView(string id) => TaskConnections.GetValueOrDefault(id) == BackendId && !_state.Tasks.Any(task => task.Id == id);
    private bool PaneVisible(string section) => _section == section;
    private int SectionCount(string section) => section switch
    {
        "Conversation" => _state.Pending.Count(pending => pending.TaskId == _selectedId),
        "Tasks" => _state.Tasks.Length,
        "Plan" => Selected?.Plan.Length ?? 0,
        "Changes" => SelectedChanges?.Files.Length ?? 0,
        "Queue" => Selected?.Queue.Messages.Length ?? 0,
        _ => 0
    };
    private string ConnectionLabel => IsDirect ? "Direct API" : IsBrowser ? "Local provider relay" : "Paired companion";
    private async Task SelectSectionAsync(string section)
    {
        _section = section;
        if (section is "Permissions" or "Tools") await LoadToolsAsync();
        if (section == "Activity") await LoadActivityAsync();
    }
    private async Task LoadToolsAsync()
    {
        if (!_connected) return;
        try { _toolCatalog = await RequestAsync<ToolView[]>("tools", new { }); }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task LoadActivityAsync()
    {
        if (!_connected) return;
        try { _agentActivity = await RequestAsync<ActivityView[]>("activity", new { }); }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task ClearAgentActivityAsync()
    {
        await CommandAsync("activity_clear", new { }); await LoadActivityAsync();
    }
    private async Task ExportAgentActivityAsync()
    {
        await LoadActivityAsync();
        await _module!.InvokeVoidAsync("download", "xamlg-agent-activity.json", JsonSerializer.Serialize(VisibleActivity), "application/json");
    }
    private void SetToolRule()
    {
        if (!_toolCatalog.Any(tool => tool.Name == _ruleTool) || _ruleDecision is not ("allow" or "ask" or "deny")) return;
        var rules = ToolRuleValues; rules[_ruleTool] = _ruleDecision; _toolRules = JsonSerializer.Serialize(rules);
    }
    private void RemoveToolRule(string name)
    { var rules = ToolRuleValues; rules.Remove(name); _toolRules = JsonSerializer.Serialize(rules); }
    private async Task ExportPendingReviewAsync(string id)
    {
        try
        {
            var text = await RequestAsync<string>("pending_export", new { id });
            await _module!.InvokeVoidAsync("download", "xamlg-operation-review.json", text, "application/json");
        }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task DeleteTaskAsync()
    {
        var id = _deleteTaskId; _deleteTaskId = null;
        if (id == null || _state.Tasks.FirstOrDefault(task => task.Id == id) is not { } task || IsRunning(task)) return;
        await CommandAsync("delete", new { id });
        if (_state.Tasks.Length == 0) _section = "Tasks";
    }
    private Task RevokeGrantAsync(string tool) => CommandAsync("revoke_grant", new { id = _state.ActivePermissions!.TaskId, text = tool });
    public sealed class ToolView
    {
        public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Scope { get; set; } = ""; public string Effect { get; set; } = "";
        public bool Destructive { get; set; }
        public JsonElement InputSchema { get; set; }
        public EffectView[]? AdditionalEffects { get; set; }
    }
    public sealed class EffectView { public string Scope { get; set; } = ""; public string Effect { get; set; } = ""; }
    public sealed class ActivePermissionView
    {
        public string TaskId { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
        public PolicyView Policy { get; set; } = new();
        public string[] GrantedTools { get; set; } = [];
    }
    public sealed class PolicyView { public string Profile { get; set; } = ""; }
    public sealed class ConstraintView
    {
        public string[] AllowedProfiles { get; set; } = Profiles; public string[] DeniedScopes { get; set; } = []; public string[] DeniedTools { get; set; } = [];
        public TimeSpan MaximumLease { get; set; } = TimeSpan.FromHours(1);
        public bool AllowRunApprovals { get; set; } = true;
    }
    public sealed class ActivityView
    {
        public long Sequence { get; set; }
        public DateTimeOffset Time { get; set; }
        public string TaskId { get; set; } = ""; public string Kind { get; set; } = ""; public string Text { get; set; } = "";
    }
}
