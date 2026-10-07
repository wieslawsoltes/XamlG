using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.AvaloniaRuntime;
using XamlG.Playground.Components;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private static readonly string[] InspectorTabs = { "C# output", "C# files", "Resources", "Syntax", "Bound tree", "Visual tree", "Properties", "Runtime", "Pipeline" };
    private XamlDocumentSession _document = new(PlaygroundExamples.All[0].Xaml, "View.axaml");
    private string _code = PlaygroundExamples.All[0].Code;
    private BrowserCompilation? _result;
    private CodeEditor? _xamlEditor;
    private CodeEditor? _codeEditor;
    private IJSObjectReference? _module;
    private AvaloniaVisualNode? _visualTree;
    private AvaloniaVisualNode? _selectedVisual;
    private XamlElementSyntax? _selectedElement;
    private string _propertyName = "Text";
    private string _propertyValue = string.Empty;
    private string _editorTab = "xaml";
    private string _inspectorTab = "C# output";
    private string _status = "Loading compiler metadata…";
    private string? _error;
    private string _theme = "dark";
    private int _exampleIndex;
    private bool _ready;
    private bool _busy;
    private bool _previewShown;
    private bool _disposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await RevealGeneratedAsync();
        if (!firstRender) return;
        try
        {
            _module = await JavaScript.InvokeAsync<IJSObjectReference>("import", "./studio.js");
            _theme = await _module.InvokeAsync<string>("loadTheme");
            await _module.InvokeVoidAsync("setTheme", _theme);
            await Compiler.InitializeAsync((current, total) =>
            {
                _status = $"Loading compiler metadata · {current} / {total}";
                _ = InvokeAsync(StateHasChanged);
            });
            await _module.InvokeVoidAsync("waitForElement", "avalonia-preview");
            await Preview.InitializeAsync("avalonia-preview", new Uri(Navigation.BaseUri));
            _ready = true;
            _automationReference = DotNetObjectReference.Create(this);
            await _module.InvokeVoidAsync("installAutomation", _automationReference);
            _status = "Ready · compile or run the project";
            await CompileSnapshotAsync();
        }
        catch (Exception error) { Report(error); }
        if (!_disposed) StateHasChanged();
    }
    private void UpdateXaml(string text)
    {
        var current = _document.Current;
        if (text == current.Text) return;
        _document.Apply(new(current.Version, "Edit source", XamlTextDiffer.GetChanges(current.Text, text)));
        _selectedElement = null;
        _status = "Source changed · compile to update inspections";
    }
    private async Task XamlChangedAsync(string text) { UpdateXaml(text); await SaveDraftAsync(); }
    private async Task CodeChangedAsync(string text)
    {
        _code = text; _status = "Code changed · compile to update inspections"; await SaveDraftAsync();
    }
    private Task CompileAsync() => CompileSnapshotAsync(captureEditors: true);
    private async Task CompileSnapshotAsync(bool captureEditors = false)
    {
        if (!_ready || _busy) return;
        _busy = true; _error = null;
        try
        {
            if (captureEditors) await CaptureEditorsAsync();
            _result = null; _status = "Compiling XAML project and C#…";
            StateHasChanged(); await Task.Yield();
            _result = Compiler.Analyze(_document.Current, _code);
            NotifyCompilerResources();
            _status = _result.Success ? $"Compilation succeeded · {_result.Project?.Documents.Length ?? 1} documents · {_result.ElapsedMilliseconds:0.0} ms" : "Compilation has errors";
            if (_xamlEditor != null) await _xamlEditor.SetDiagnosticsAsync(_result.Diagnostics.Where(d => d.Path == "View.axaml"));
            if (_codeEditor != null) await _codeEditor.SetDiagnosticsAsync(_result.Diagnostics.Where(d => d.Path == "Code.cs"));
        }
        catch (Exception error) { _result = null; Report(error); }
        finally { _busy = false; }
    }
    private async Task CaptureEditorsAsync()
    {
        if (_xamlEditor != null) UpdateXaml(await _xamlEditor.GetTextAsync());
        if (_codeEditor != null) _code = await _codeEditor.GetTextAsync();
        if (_resourceEditor != null) await _resourceEditor.CaptureAsync();
        if (_projectCodeEditor != null) await _projectCodeEditor.CaptureAsync();
        await SaveDraftAsync();
    }
    private async Task RunAsync()
    {
        if (!_ready || _busy) return;
        await CompileAsync();
        if (_result?.Success != true) return;
        _busy = true;
        try
        {
            _visualTree = await Preview.ShowAsync(Compiler.Run(_result));
            _automation.NotifyResourceChanged("xamlg://runtime");
            _previewShown = true; _status = "Preview running · actual Avalonia visual tree available";
        }
        catch (Exception error) { Report(error); }
        finally { _busy = false; }
    }
    private async Task SelectExampleAsync(ChangeEventArgs args)
    {
        if (_busy || !int.TryParse(args.Value?.ToString(), out var index) || index < 0 || index >= PlaygroundExamples.All.Count) return;
        await RetireAutomationWorkspaceAsync();
        _exampleIndex = index;
        var example = PlaygroundExamples.All[index];
        Compiler.Resources.ReplaceAll(new Dictionary<string, string>());
        Compiler.CodeFiles.ReplaceAll(new Dictionary<string, string>());
        _document = new(example.Xaml, "View.axaml"); _code = example.Code;
        _selectedElement = null; _error = null; _result = null;
        ResetWorkspaceHistory();
        await CompileSnapshotAsync();
    }
    private async Task SelectSyntaxAsync(XamlInspectionNode node)
    {
        if (_result == null || !ReferenceEquals(_result.Analysis.Syntax, _document.Current))
        { _status = "Source changed · compile before selecting an inspection node"; return; }
        _selectedElement = _document.Current.FindElement(node.Span.Start); _editorTab = "xaml";
        if (_xamlEditor != null) await _xamlEditor.RevealAsync(node.Span);
        if (_selectedElement != null)
        {
            var attribute = _selectedElement.Attributes.FirstOrDefault(a => !a.IsNamespace);
            if (attribute != null) { _propertyName = attribute.Name; _propertyValue = attribute.Value; }
        }
    }
    private void SelectVisual(AvaloniaVisualNode node) { _selectedVisual = node; _inspectorTab = "Properties"; }
    private void RefreshVisuals() { try { _visualTree = Preview.Inspect(); } catch (Exception error) { Report(error); } }
    private async Task ApplyPropertyAsync()
    {
        if (_selectedElement == null || _busy) return;
        try
        {
            var position = _selectedElement.Span.Start;
            _document.Apply(XamlDesignerEdits.SetProperty(_document.Current, _selectedElement, _propertyName, _propertyValue), requireWellFormed: true);
            _selectedElement = _document.Current.FindElement(position);
            await SaveDraftAsync(); await CompileSnapshotAsync();
        }
        catch (Exception error) { Report(error); }
    }
    private Task UndoAsync() => NavigateWorkspaceAsync(true);
    private Task RedoAsync() => NavigateWorkspaceAsync(false);
    private async Task ToggleThemeAsync()
    {
        _theme = _theme == "dark" ? "light" : "dark";
        if (_module != null) await _module.InvokeVoidAsync("setTheme", _theme);
    }
    private Dictionary<string, string> ResourceTexts() => Compiler.Resources.Snapshot.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);
    private async Task SaveDraftAsync()
    {
        RecordWorkspace();
        if (_module != null) await _module.InvokeVoidAsync("saveDraft", _document.Current.Text, _code, ResourceTexts(), CodeTexts());
    }
    private async Task RestoreDraftAsync()
    {
        if (_module == null || _busy) return;
        try
        {
            var value = await _module.InvokeAsync<JsonElement?>("loadDraft");
            if (value is not { ValueKind: JsonValueKind.Object } draft) { _status = "No saved draft in this browser"; return; }
            var resources = draft.TryGetProperty("resources", out var source) && source.ValueKind == JsonValueKind.Object
                ? source.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : new Dictionary<string, string>();
            var documents = new Dictionary<string, string>(resources, StringComparer.Ordinal);
            if (draft.TryGetProperty("codeFiles", out var codeFiles) && codeFiles.ValueKind == JsonValueKind.Object)
                foreach (var item in codeFiles.EnumerateObject())
                {
                    if (!IsCSharpPath(item.Name)) throw new ArgumentException("Only C# documents belong in codeFiles.");
                    documents.Add(item.Name, item.Value.GetString() ?? string.Empty);
                }
            documents.Add("View.axaml", draft.GetProperty("xaml").GetString() ?? string.Empty);
            documents.Add("Code.cs", draft.GetProperty("code").GetString() ?? string.Empty);
            ValidateWorkspace(documents);
            await RetireAutomationWorkspaceAsync();
            RestoreWorkspace(_workspaceEdits.ReplaceAll(SourceRevision, documents, "Restore draft", recordHistory: false));
            ResetWorkspaceHistory();
            await CompileSnapshotAsync();
            _status = "Draft restored without executing it · review the code before Run";
        }
        catch (Exception error) { Report(error); }
    }
    private async Task ExportAsync()
    {
        if (_module == null || _busy) return;
        await CaptureEditorsAsync();
        var current = _result?.Success == true && ReferenceEquals(_result.Analysis.Syntax, _document.Current) &&
            _result.ResourceRevision == Compiler.Resources.Revision && _result.CodeRevision == Compiler.CodeFiles.Revision && _result.CodeText == _code;
        var content = JsonSerializer.Serialize(new
        {
            format = "xamlg-project", version = 3, xaml = _document.Current.Text, code = _code, resources = ResourceTexts(), codeFiles = CodeTexts(),
            generated = current ? _result!.Analysis.Output.Source : null,
            generatedFiles = current ? _result!.Project?.Documents.ToDictionary(d => d.Output.HintName, d => d.Output.Source, StringComparer.Ordinal) : null
        }, new JsonSerializerOptions { WriteIndented = true });
        await _module.InvokeVoidAsync("download", "xamlg-project.json", content, "application/json");
    }
    private async Task RevealDiagnosticAsync(PlaygroundDiagnostic diagnostic)
    {
        if (Compiler.CodeFiles.Snapshot.ContainsKey(diagnostic.Path))
        {
            if (_projectCodeEditor != null) { await _projectCodeEditor.CaptureAsync(); _projectCodeEditor.SelectDocument(diagnostic.Path); }
            _inspectorTab = "C# files"; return;
        }
        if (Compiler.Resources.Snapshot.ContainsKey(diagnostic.Path))
        {
            if (_resourceEditor != null) { await _resourceEditor.CaptureAsync(); _resourceEditor.SelectDocument(diagnostic.Path); }
            _inspectorTab = "Resources"; return;
        }
        var code = diagnostic.Path == "Code.cs"; _editorTab = code ? "code" : "xaml";
        var source = code ? _code : _document.Current.Text; var map = new SourceLineMap(source);
        try
        {
            var start = map.GetOffset(new(diagnostic.StartLine - 1, diagnostic.StartColumn - 1));
            var editor = code ? _codeEditor : _xamlEditor;
            if (editor != null) await editor.RevealAsync(new(start, Math.Min(1, source.Length - start)));
        }
        catch (ArgumentOutOfRangeException) { }
    }
    private void Report(Exception error)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        _error = error.GetType().Name + ": " + error.Message;
        _status = "Operation failed · previous preview retained where possible";
    }
    public async ValueTask DisposeAsync()
    {
        RevokeAutomation(); _runtimeInspector?.Dispose(); _buildArtifacts.Dispose();
        if (_module != null) await _module.InvokeVoidAsync("disconnectAutomation");
        _automationReference?.Dispose();
        _disposed = true; Preview.Dispose();
        if (_module != null) await _module.DisposeAsync();
    }
}
