using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;
using XamlG.Runtime;

namespace XamlG.Playground.Components;

public partial class RuntimeWorkbench : IDisposable
{
    [Parameter, EditorRequired] public Func<string, JsonElement, CancellationToken, Task<JsonElement>> Execute { get; set; } = default!;
    [Parameter] public IReadOnlyList<AutomationTool> Tools { get; set; } = [];
    [Parameter] public EventCallback<XamlSourceInfo> SourceRequested { get; set; }
    [Parameter] public long PreviewRevision { get; set; }
    [Parameter] public long SourceRevision { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, RuntimeNode> _nodes = new(StringComparer.Ordinal);
    private RuntimeSnapshot? _snapshot;
    private RuntimeProperty[] _properties = [];
    private RuntimeAccessibilitySnapshot? _accessibility;
    private RuntimeAccessibilityProvider? _provider;
    private JsonElement? _details;
    private string _selectedId = "", _treeFilter = "", _treeMode = "visual", _propertyFilter = "", _propertyKey = "", _propertyJson = "null", _classes = "";
    private string _panel = "Properties", _key = "Enter", _text = "", _button = "Left", _modifiers = "", _objectPath = "DataContext", _method = "", _methodArguments = "[]";
    private string _peerId = "", _providerName = "", _providerMethod = "", _providerArguments = "[]", _event = "";
    private string _toolName = "", _toolArguments = "{}", _toolFilter = "";
    private string? _error;
    private double? _x, _y;
    private double _wheelX, _wheelY = -1;
    private long _revision, _observedPreview = long.MinValue, _changeSequence;
    private bool _busy, _disposed, _refreshAfterBusy;
    private static readonly string[] Panels = ["Properties", "Objects", "Bindings", "Styles", "Resources", "Events", "Input", "Accessibility", "Tools"];
    private RuntimeNode? Selected => _nodes.GetValueOrDefault(_selectedId);
    private RuntimeProperty? SelectedProperty => _properties.FirstOrDefault(property => property.Key == _propertyKey);
    private RuntimeAccessibilityNode? SelectedPeer => _accessibility?.Nodes.FirstOrDefault(peer => peer.Id == _peerId);
    private IEnumerable<RuntimeNode> VisibleNodes => (_snapshot?.Nodes ?? []).Where(node =>
        node.Type.Contains(_treeFilter, StringComparison.OrdinalIgnoreCase) || node.Name?.Contains(_treeFilter, StringComparison.OrdinalIgnoreCase) == true || node.Id.Contains(_treeFilter, StringComparison.OrdinalIgnoreCase));
    private IEnumerable<AutomationTool> RuntimeTools => Tools.Where(tool => tool.Scope == AutomationScope.Runtime &&
        (tool.Name.Contains(_toolFilter, StringComparison.OrdinalIgnoreCase) || tool.Description.Contains(_toolFilter, StringComparison.OrdinalIgnoreCase)));
    private AutomationTool? SelectedTool => Tools.FirstOrDefault(tool => tool.Name == _toolName);
    private string[] Modifiers => _modifiers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private string[] ObjectPath => _objectPath.Length == 0 ? [] : _objectPath.Split('.', StringSplitOptions.TrimEntries);
    private static string Pretty(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(AutomationJson.Options) { WriteIndented = true });
    private static string Display(RuntimeValue? value) => value == null ? "" : value.Value is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : JsonSerializer.Serialize(value.Value);
    private static string Excerpt(string text) => text.Length <= 65536 ? text : text[..65536] + "\n[display excerpt; export the result for complete content]";
    private static string NodeTitle(RuntimeNode node) => (string.IsNullOrEmpty(node.Name) ? "" : node.Name + " · ") + node.Type.Split('.').Last();
    private string PeerTitle(RuntimeAccessibilityNode peer) => Display(peer.Properties.GetValueOrDefault("name")) + " · " + Display(peer.Properties.GetValueOrDefault("controlType"));
    private int Depth(RuntimeNode node)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var depth = 0;
        while (depth < 24 && (_treeMode == "visual" ? node.VisualParent : node.LogicalParent) is { } parent && _nodes.TryGetValue(parent, out node!) && seen.Add(parent)) depth++;
        return depth;
    }
    protected override async Task OnParametersSetAsync()
    {
        if (_observedPreview == PreviewRevision) return;
        _observedPreview = PreviewRevision; _snapshot = null; _nodes.Clear(); _selectedId = ""; _properties = []; _details = null; _accessibility = null; _provider = null; _changeSequence = 0;
        if (PreviewRevision >= 0)
        { if (_busy) _refreshAfterBusy = true; else await RefreshAsync(); }
    }
    private async Task Guard(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _error = null;
        try { await action(); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { _error = error.Message; }
        finally
        {
            _busy = false;
            if (_refreshAfterBusy && !_disposed) { _refreshAfterBusy = false; await RefreshAsync(); }
        }
    }
    private async Task<JsonElement> CallAsync(string name, object arguments)
    {
        var preview = PreviewRevision;
        var result = await Execute(name, arguments is JsonElement json ? json : AutomationJson.Element(arguments), _lifetime.Token);
        if (preview != PreviewRevision) throw new InvalidOperationException("The preview was replaced while the operation was running. Refresh the runtime before editing again.");
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("revision", out var revision)) _revision = revision.GetInt64();
        return result;
    }
    private Task RefreshAsync() => Guard(RefreshCoreAsync);
    private async Task RefreshCoreAsync()
    {
        _snapshot = (await CallAsync("xamlg_runtime_tree", new { })).Deserialize<RuntimeSnapshot>(AutomationJson.Options)!;
        _nodes.Clear(); foreach (var node in _snapshot.Nodes) _nodes[node.Id] = node;
        if (!_nodes.ContainsKey(_selectedId)) _selectedId = _snapshot.RootId;
        _classes = string.Join(' ', Selected?.Classes.Where(value => !value.StartsWith(':')) ?? []);
        await ReadPropertiesAsync();
    }
    private Task SelectNodeAsync(string id) => Guard(async () =>
    { _selectedId = id; _propertyKey = ""; _details = null; _method = ""; _classes = string.Join(' ', Selected?.Classes.Where(value => !value.StartsWith(':')) ?? []); await ReadPropertiesAsync(); });
    private async Task ReadPropertiesAsync()
    {
        _properties = (await CallAsync("xamlg_runtime_properties", new { objectId = _selectedId })).GetProperty("properties").Deserialize<RuntimeProperty[]>(AutomationJson.Options)!;
    }
    private void SelectProperty(RuntimeProperty property) { _propertyKey = property.Key; _propertyJson = Pretty(property.Value?.Value); }
    private Task MutateAsync(string name, object arguments) => Guard(async () => { _details = await CallAsync(name, arguments); await RefreshCoreAsync(); });
    private Task SetPropertyAsync() => Guard(async () =>
    {
        var value = JsonSerializer.Deserialize<JsonElement>(_propertyJson);
        _details = await CallAsync("xamlg_runtime_property_set", new { objectId = _selectedId, property = _propertyKey, value, expectedRevision = _revision });
        await RefreshCoreAsync();
    });
    private Task ClearPropertyAsync() => MutateAsync("xamlg_runtime_property_clear", new { objectId = _selectedId, property = _propertyKey, expectedRevision = _revision });
    private Task SetClassesAsync() => MutateAsync("xamlg_runtime_classes_set", new { objectId = _selectedId, classes = _classes.Split(' ', StringSplitOptions.RemoveEmptyEntries), expectedRevision = _revision });
    private Task FocusAsync() => MutateAsync("xamlg_runtime_focus", new { objectId = _selectedId, expectedRevision = _revision });
    private Task LayoutAsync() => MutateAsync("xamlg_runtime_layout", new { objectId = _selectedId, expectedRevision = _revision });
    private Task BringIntoViewAsync() => MutateAsync("xamlg_runtime_bring_into_view", new { objectId = _selectedId, expectedRevision = _revision });
    private Task ReadDetailsAsync(string suffix) => Guard(async () => { _details = await CallAsync("xamlg_runtime_" + suffix, new { objectId = _selectedId }); });
    private Task InspectObjectAsync() => Guard(async () => { _details = await CallAsync("xamlg_runtime_object_inspect", new { objectId = _selectedId, path = ObjectPath }); });
    private Task InvokeMethodAsync() => Guard(async () =>
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(_methodArguments);
        _details = await CallAsync("xamlg_runtime_method_invoke", new { objectId = _selectedId, path = ObjectPath, signature = _method, arguments, expectedRevision = _revision });
        await RefreshCoreAsync();
    });
    private Task SendKeyAsync() => MutateAsync("xamlg_runtime_input_key", new { objectId = _selectedId, key = _key, modifiers = Modifiers, expectedRevision = _revision });
    private Task SendTextAsync() => MutateAsync("xamlg_runtime_input_text", new { objectId = _selectedId, text = _text, expectedRevision = _revision });
    private Task PointerAsync(RuntimePointerAction action) => MutateAsync("xamlg_runtime_input_pointer", new { objectId = _selectedId, action, x = _x, y = _y, button = _button, modifiers = Modifiers, deltaX = _wheelX, deltaY = _wheelY, expectedRevision = _revision });
    private Task ResetInputAsync() => MutateAsync("xamlg_runtime_input_reset", new { expectedRevision = _revision });
    private Task WatchEventAsync() => Guard(async () => { _details = await CallAsync("xamlg_runtime_event_watch", new { objectId = _selectedId, @event = _event }); });
    private Task RaiseEventAsync() => MutateAsync("xamlg_runtime_event_raise", new { objectId = _selectedId, @event = _event, expectedRevision = _revision });
    private Task ReadChangesAsync() => Guard(async () =>
    {
        _details = await CallAsync("xamlg_runtime_changes", new { afterSequence = _changeSequence });
        _changeSequence = _details.Value.GetProperty("sequence").GetInt64();
    });
    private Task ReadAccessibilityAsync(int offset = 0) => Guard(async () =>
    {
        _accessibility = (await CallAsync("xamlg_runtime_accessibility", new { offset, count = 100 })).Deserialize<RuntimeAccessibilitySnapshot>(AutomationJson.Options)!;
        if (SelectedPeer == null) _peerId = _accessibility.Nodes.FirstOrDefault()?.Id ?? "";
        _provider = null; _providerName = "";
    });
    private void SelectPeer(ChangeEventArgs args) { _peerId = args.Value?.ToString() ?? ""; _provider = null; _providerName = ""; }
    private Task ReadProviderAsync() => Guard(async () =>
    {
        _provider = (await CallAsync("xamlg_runtime_accessibility_provider", new { peerId = _peerId, provider = _providerName })).Deserialize<RuntimeAccessibilityProvider>(AutomationJson.Options)!;
        _details = AutomationJson.Element(_provider); _providerMethod = _provider.Methods.FirstOrDefault() ?? "";
    });
    private Task InvokeProviderAsync() => Guard(async () =>
    {
        var arguments = JsonSerializer.Deserialize<JsonElement>(_providerArguments);
        _details = await CallAsync("xamlg_runtime_accessibility_invoke", new { peerId = _peerId, provider = _providerName, signature = _providerMethod, arguments, expectedRevision = _revision });
        await RefreshCoreAsync();
    });
    private Task AccessibilityActionAsync(RuntimeAccessibilityAction action) => MutateAsync("xamlg_runtime_accessibility_action", new { peerId = _peerId, action, expectedRevision = _revision });
    private void SelectTool(ChangeEventArgs args) { _toolName = args.Value?.ToString() ?? ""; PrepareToolArguments(); }
    private void PrepareToolArguments()
    {
        if (SelectedTool == null) return;
        var arguments = RequiredArguments(SelectedTool.InputSchema);
        if (SelectedTool.InputSchema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
                switch (property.Name)
                {
                    case "objectId": case "parentId": arguments[property.Name] = _selectedId; break;
                    case "peerId": arguments[property.Name] = _peerId; break;
                    case "expectedRevision": arguments[property.Name] = _toolName == "xamlg_runtime_run" ? SourceRevision : _revision; break;
                    case "property" when _propertyKey.Length > 0: arguments[property.Name] = _propertyKey; break;
                }
        _toolArguments = arguments.ToJsonString(new() { WriteIndented = true });
    }
    private static JsonObject RequiredArguments(JsonElement schema)
    {
        var result = new JsonObject();
        if (!schema.TryGetProperty("required", out var required) || !schema.TryGetProperty("properties", out var properties)) return result;
        foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
        {
            var property = properties.GetProperty(name);
            result[name] = property.TryGetProperty("enum", out var values) ? JsonNode.Parse(values[0].GetRawText()) :
                property.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() switch
                { "string" => JsonValue.Create(""), "integer" or "number" => JsonValue.Create(0), "boolean" => JsonValue.Create(false), "array" => new JsonArray(), "object" => new JsonObject(), _ => null } : null;
        }
        return result;
    }
    private Task ExecuteToolAsync() => Guard(async () =>
    {
        _details = await CallAsync(_toolName, JsonSerializer.Deserialize<JsonElement>(_toolArguments));
        await RefreshCoreAsync();
    });
    private async Task ExportResultAsync()
    {
        if (_details == null) return;
        await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
        await module.InvokeVoidAsync("download", "xamlg-runtime-inspection.json", Pretty(_details), "application/json");
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
