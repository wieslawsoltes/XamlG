using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.Tooling;

namespace XamlG.Playground.Components;

public partial class CompilerWorkbench : IDisposable
{
    [Parameter, EditorRequired] public Func<string, JsonElement, CancellationToken, Task<JsonElement>> Execute { get; set; } = default!;
    [Parameter] public IReadOnlyList<AutomationTool> Tools { get; set; } = [];
    [Parameter] public long SourceRevision { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private App.CompilerOptionsSnapshot? _state;
    private CSharpCompilationSettings? _model;
    private JsonElement? _result;
    private string _text = "{}", _baseText = "", _referenceFilter = "", _toolFilter = "", _toolName = "", _toolArguments = "{}";
    private string? _error;
    private long _baseRevision, _observedRevision = -1;
    private bool _dirty, _busy, _disposed, _refreshPending;
    private static readonly (string Key, string Title)[] StringOptions =
    [
        ("languageVersion", "Language version"), ("nullable", "Nullable context"), ("optimization", "Optimization"),
        ("outputKind", "Output kind"), ("platform", "Platform"), ("metadataImport", "Metadata visibility"),
        ("documentationMode", "Documentation parsing"), ("generalDiagnostic", "General diagnostic policy")
    ];
    private static readonly (string Key, string Title)[] BooleanOptions =
    [
        ("allowUnsafe", "Allow unsafe code"), ("checkOverflow", "Check arithmetic overflow"), ("deterministic", "Deterministic emission"),
        ("concurrentBuild", "Concurrent compilation"), ("reportSuppressedDiagnostics", "Report suppressed diagnostics")
    ];
    private IEnumerable<AutomationTool> CompilerTools => Tools.Where(tool => tool.Scope == AutomationScope.Compiler &&
        (tool.Name.Contains(_toolFilter, StringComparison.OrdinalIgnoreCase) || tool.Description.Contains(_toolFilter, StringComparison.OrdinalIgnoreCase)));
    private AutomationTool? SelectedTool => Tools.FirstOrDefault(tool => tool.Name == _toolName);
    private static string Pretty(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(AutomationJson.Options) { WriteIndented = true });
    private static string Excerpt(string value) => value.Length <= 65536 ? value : value[..65536] + "\n[display excerpt; export the full result]";
    private object? Option(string key) => _model == null ? null : typeof(CSharpCompilationSettings).GetProperty(char.ToUpperInvariant(key[0]) + key[1..])?.GetValue(_model);
    protected override async Task OnParametersSetAsync()
    {
        if (_observedRevision == SourceRevision) return;
        _observedRevision = SourceRevision;
        if (_busy) _refreshPending = true; else await Guard(() => RefreshCoreAsync(false));
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
            if (_refreshPending && !_disposed) { _refreshPending = false; await Guard(() => RefreshCoreAsync(false)); }
        }
    }
    private Task<JsonElement> CallAsync(string name, object arguments) => Execute(name,
        arguments is JsonElement json ? json : AutomationJson.Element(arguments), _lifetime.Token);
    private async Task RefreshCoreAsync(bool discardDraft)
    {
        _state = (await CallAsync("xamlg_compiler_options_get", new { })).Deserialize<App.CompilerOptionsSnapshot>(AutomationJson.Options)!;
        if (!_dirty || discardDraft)
        { _text = _baseText = _state.Text; _baseRevision = _state.Revision; _dirty = false; ReadModel(); }
        else if (_baseText == _state.Text) _baseRevision = _state.Revision;
    }
    private void DraftChanged() { _dirty = _text != _baseText; ReadModel(); }
    private void ReadModel()
    {
        try { _model = JsonSerializer.Deserialize<CSharpCompilationSettings>(_text, AutomationJson.Options); }
        catch (JsonException) { _model = null; }
    }
    private void SetOption<T>(string key, T value)
    {
        try
        {
            var root = JsonNode.Parse(_text) as JsonObject ?? throw new InvalidOperationException("Complete a JSON object before using the option controls.");
            root[key] = JsonSerializer.SerializeToNode(value, AutomationJson.Options);
            _text = root.ToJsonString(new JsonSerializerOptions(AutomationJson.Options) { WriteIndented = true }); DraftChanged();
        }
        catch (Exception error) { _error = error.Message; }
    }
    private void SetSymbols(ChangeEventArgs args) => SetOption("preprocessorSymbols", (args.Value?.ToString() ?? "").Split(
        [',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    private void SetWarningLevel(ChangeEventArgs args)
    { if (int.TryParse(args.Value?.ToString(), out var value)) SetOption("warningLevel", value); }
    private void SetAllReferences(ChangeEventArgs args) => SetOption<IReadOnlyList<string>?>("referenceNames", args.Value is true ? null : _state?.AvailableReferences ?? []);
    private void SetReference(string name, ChangeEventArgs args)
    {
        var names = (_model?.ReferenceNames ?? _state?.AvailableReferences ?? []).ToHashSet(StringComparer.Ordinal);
        if (args.Value is true) names.Add(name); else names.Remove(name);
        SetOption("referenceNames", names.Order(StringComparer.Ordinal).ToArray());
    }
    private Task ApplyAsync() => Guard(async () =>
    {
        var state = (await CallAsync("xamlg_compiler_options_write", new { text = _text, expectedRevision = _baseRevision })).Deserialize<App.CompilerOptionsSnapshot>(AutomationJson.Options)!;
        _state = state; _text = _baseText = state.Text; _baseRevision = state.Revision; _dirty = false; ReadModel();
        _result = AutomationJson.Element(new { saved = true, state.Revision, state.SettingsRevision });
    });
    private Task CompileAsync() => Guard(async () => { _result = await CallAsync("xamlg_compiler_compile", new { }); });
    private void SelectTool(ChangeEventArgs args) { _toolName = args.Value?.ToString() ?? ""; FillToolArguments(); }
    private void FillToolArguments()
    {
        if (SelectedTool == null) return;
        var result = new JsonObject(); var schema = SelectedTool.InputSchema;
        if (schema.TryGetProperty("properties", out var properties))
        {
            var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name == "expectedRevision") result[property.Name] = SourceRevision;
                else if (property.Name == "path") result[property.Name] = "Code.cs";
                else if (required.Contains(property.Name)) result[property.Name] = property.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() switch
                { "integer" or "number" => JsonValue.Create(0), "boolean" => JsonValue.Create(false), "array" => new JsonArray(), "object" => new JsonObject(), "string" => JsonValue.Create(""), _ => null } : null;
            }
        }
        _toolArguments = result.ToJsonString(new() { WriteIndented = true });
    }
    private Task ExecuteToolAsync() => Guard(async () =>
    { _result = await CallAsync(_toolName, JsonSerializer.Deserialize<JsonElement>(_toolArguments)); await RefreshCoreAsync(false); });
    private async Task ExportResultAsync()
    {
        if (_result == null) return;
        await using var module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
        await module.InvokeVoidAsync("download", "xamlg-compiler-inspection.json", Pretty(_result), "application/json");
    }
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
