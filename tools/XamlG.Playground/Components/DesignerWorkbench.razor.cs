using System.Text.Json;
using Microsoft.AspNetCore.Components;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;
using XamlG.Runtime;
using XamlG.Runtime.Design;

namespace XamlG.Playground.Components;

public partial class DesignerWorkbench : IDisposable
{
    [Parameter, EditorRequired] public Func<string, JsonElement, CancellationToken, Task<JsonElement>> Execute { get; set; } = default!;
    [Parameter] public long SourceRevision { get; set; }
    [Parameter] public long PreviewRevision { get; set; }
    [Parameter] public long DesignerRevision { get; set; }
    [Parameter] public bool HostBusy { get; set; }
    [Parameter] public EventCallback<XamlSourceInfo> SourceRequested { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
    private App.DesignerWorkspaceState? _state;
    private RuntimeNode[] _nodes = [];
    private App.DesignerSourcePlan? _plan;
    private JsonElement _planArguments;
    private string _planCommand = "", _filter = "", _anchor = "";
    private string? _error;
    private double _grid = 8, _x, _y, _width, _height;
    private XamlDesignArrangement _arrangement;
    private bool _busy, _disposed, _refreshPending;
    private bool Busy => _busy || HostBusy;
    private (long Source, long Preview, long Design)? _observed;
    private IEnumerable<RuntimeNode> SelectedNodes => _nodes.Where(node => _picked.Contains(node.Id));
    private IEnumerable<RuntimeNode> VisibleNodes => _nodes.Where(node => node.Type.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        node.Name?.Contains(_filter, StringComparison.OrdinalIgnoreCase) == true || node.Source?.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase) == true);
    private bool CanPlan => !Busy && _state is { PreviewMatchesSource: true, Isolated: false, GestureActive: false, RuntimeRevision: not null } && _picked.Count != 0;
    private bool CanApply => !Busy && _plan is { Applied: false, Documents.Count: > 0 } && _plan.SourceRevision == SourceRevision && _plan.RuntimeRevision == _state?.RuntimeRevision;
    private static string Title(RuntimeNode node) => (node.Name == null ? "" : node.Name + " · ") + node.Type.Split('.').Last();
    private static XamlDesignRect Bounds(RuntimeNode node) => node.Bounds is { } bounds ? new(bounds.RootX, bounds.RootY, bounds.Width, bounds.Height) : throw new InvalidOperationException("The selected control has no realized bounds.");
    private static string Pretty(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(AutomationJson.Options) { WriteIndented = true });
    protected override async Task OnParametersSetAsync()
    {
        // Undo/compile can publish the new source revision before the host is
        // ready to serve it. Do not mark that revision refreshed after a rejected
        // read; the host's busy-to-idle transition must trigger the pending read.
        if (HostBusy) return;
        var current = (SourceRevision, PreviewRevision, DesignerRevision);
        if (_observed == current) return;
        _observed = current;
        if (_busy) _refreshPending = true; else await RefreshAsync();
    }
    private async Task Guard(Func<Task> action)
    {
        if (Busy || _disposed) return;
        _busy = true; _error = null;
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { _error = error.Message; }
        finally
        {
            _busy = false;
            if (_refreshPending && !_disposed) { _refreshPending = false; await RefreshAsync(); }
        }
    }
    private Task<JsonElement> CallAsync(string name, object arguments) => Execute(name,
        arguments is JsonElement json ? json : AutomationJson.Element(arguments), _lifetime.Token);
    private Task RefreshAsync() => Guard(async () =>
    {
        try { await RefreshCoreAsync(); }
        catch { _observed = null; throw; }
    });
    private async Task RefreshCoreAsync()
    {
        var previous = _state;
        var state = (await CallAsync("xamlg_designer_state", new { })).Deserialize<App.DesignerWorkspaceState>(AutomationJson.Options)!;
        var tree = state.PreviewRevision == null ? null : (await CallAsync("xamlg_runtime_tree", new { })).Deserialize<RuntimeSnapshot>(AutomationJson.Options)!;
        var nodes = tree?.Nodes.Where(node => node.IsSourceOwned && node.Source != null).ToArray() ?? [];
        // A preview can be replaced while an editor flush is awaiting JS. Refresh
        // instead of associating handles from one runtime with another's state.
        var latest = (await CallAsync("xamlg_designer_state", new { })).Deserialize<App.DesignerWorkspaceState>(AutomationJson.Options)!;
        if (state.PreviewRevision != latest.PreviewRevision || tree?.SessionId != latest.RuntimeSessionId)
        { _state = latest; _nodes = []; _picked.Clear(); _plan = null; _refreshPending = true; return; }
        _state = latest with { RuntimeRevision = tree?.Revision }; _nodes = nodes; _grid = latest.GridSize;
        if (previous == null || previous.DesignerRevision != latest.DesignerRevision || previous.PreviewRevision != latest.PreviewRevision)
        { _picked.Clear(); foreach (var id in latest.SelectedObjectIds) _picked.Add(id); }
        _picked.IntersectWith(nodes.Select(node => node.Id));
        if (!_picked.Contains(_anchor)) _anchor = SelectedNodes.FirstOrDefault()?.Id ?? "";
        FillBounds();
    }
    private void Pick(string id, ChangeEventArgs args)
    {
        if (args.Value is true)
        { if (_picked.Count >= 256 && !_picked.Contains(id)) { _error = "Select at most 256 controls."; return; } _picked.Add(id); }
        else _picked.Remove(id);
        if (!_picked.Contains(_anchor)) _anchor = SelectedNodes.FirstOrDefault()?.Id ?? "";
        ClearPlan(); FillBounds();
    }
    private void FillBounds()
    {
        var bounds = SelectedNodes.Where(node => node.Bounds != null).Select(Bounds).ToArray();
        if (bounds.Length == 0) return;
        var union = XamlDesignGeometry.Union(bounds); _x = union.X; _y = union.Y; _width = union.Width; _height = union.Height;
    }
    private void ClearPlan() { _plan = null; _planCommand = ""; }
    private Task ConfigureAsync(bool? enabled = null, bool cancelGesture = false) => Guard(async () =>
    {
        if (_state == null) return;
        await CallAsync("xamlg_designer_configure", new { expectedDesignerRevision = _state.DesignerRevision, enabled, gridSize = _grid, cancelGesture });
        await RefreshCoreAsync();
    });
    private Task SelectAsync(bool clear = false) => Guard(async () =>
    {
        if (_state?.RuntimeRevision == null) return;
        await CallAsync("xamlg_designer_select", new { expectedDesignerRevision = _state.DesignerRevision,
            expectedRuntimeRevision = _state.RuntimeRevision.Value, objectIds = clear ? [] : SelectedNodes.Select(node => node.Id).ToArray() });
        ClearPlan(); await RefreshCoreAsync();
    });
    private Task PlanGeometryAsync() => Guard(async () =>
    {
        if (_state?.RuntimeRevision == null) return;
        var nodes = SelectedNodes.ToArray(); var before = nodes.Select(Bounds).ToArray();
        var after = XamlDesignGeometry.Transform(before, XamlDesignGeometry.Union(before), new(_x, _y, _width, _height));
        await PlanAsync("xamlg_designer_geometry_plan", new App.DesignerGeometryArguments(nodes.Select((node, index) => new RuntimeDesignGeometry(node.Id, after[index])).ToArray(), _state.SourceRevision, _state.RuntimeRevision.Value));
    });
    private Task PlanArrangeAsync() => Guard(async () =>
    {
        if (_state?.RuntimeRevision == null) return;
        await PlanAsync("xamlg_designer_arrange_plan", new App.DesignerArrangeArguments(SelectedNodes.Select(node => node.Id).ToArray(), _arrangement,
            _state.SourceRevision, _state.RuntimeRevision.Value, _anchor.Length == 0 ? null : _anchor));
    });
    private async Task PlanAsync(string command, object arguments)
    {
        ClearPlan(); _planArguments = AutomationJson.Element(arguments);
        _plan = (await CallAsync(command, _planArguments)).Deserialize<App.DesignerSourcePlan>(AutomationJson.Options)!;
        _planCommand = command;
    }
    private Task ApplyPlanAsync() => Guard(async () =>
    {
        if (_plan == null || _plan.Applied || _planCommand.Length == 0) return;
        _plan = (await CallAsync(_planCommand.Replace("_plan", "_apply", StringComparison.Ordinal), _planArguments)).Deserialize<App.DesignerSourcePlan>(AutomationJson.Options)!;
        await RefreshCoreAsync();
    });
    private Task ReloadAsync() => Guard(async () =>
    {
        if (_state == null) return;
        await CallAsync("xamlg_runtime_run", new { expectedRevision = _state.SourceRevision });
        ClearPlan(); await RefreshCoreAsync();
    });
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
