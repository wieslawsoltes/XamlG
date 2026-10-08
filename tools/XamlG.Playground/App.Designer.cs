#nullable enable
using System.Collections.Immutable;
using XamlG.AvaloniaRuntime;
using XamlG.Runtime;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private bool _designMode;
    private int _reparentDestination = -1;
    private string _newElementMarkup = "<Button Content=\"New button\" />";
    private XamlSyntaxTree? _selectedDesignerSyntax;
    private long _designerSelectionRequest;
    private XamlSyntaxTree SelectedDesignerSyntax => _selectedDesignerSyntax ?? _document.Current;

    protected override void OnInitialized()
    {
        InitializeAutomation();
        Preview.SourceSelected = source => InvokeAsync(() => SelectVisualSourceAsync(source));
        Preview.EditsRequested = edits => InvokeAsync(() => CommitVisualEditsAsync(edits));
        Preview.DesignStateChanged = () => { _automation.NotifyResourceChanged("xamlg://designer"); if (!_disposed) _ = InvokeAsync(StateHasChanged); };
        Preview.Error = error => { Report(error); _ = InvokeAsync(StateHasChanged); };
    }
    private void ToggleDesignMode()
    {
        if (_isolationVisible) return;
        _designerSelectionRequest++;
        _designMode = !_designMode; Preview.IsDesignMode = _designMode;
        _status = _designMode ? "Design mode · Ctrl/⌘ click selects a group · drag and resize · Shift locks aspect · Alt disables snapping · Escape cancels" : "Interaction mode";
    }
    private XamlSyntaxTree DesignerSyntax(string path) => path == _document.Current.Path ? _document.Current :
        Compiler.Resources.Snapshot.GetValueOrDefault(path) ?? throw new InvalidOperationException("The XAML source document no longer exists.");
    private Dictionary<string, XamlSyntaxTree> DesignerDocuments() => Compiler.Resources.Snapshot.SetItem(_document.Current.Path, _document.Current)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private async Task SelectVisualSourceAsync(XamlSourceInfo source)
    {
        if (_isolationVisible) return;
        var request = ++_designerSelectionRequest; var root = Preview.Root;
        await CaptureEditorsAsync();
        if (request != _designerSelectionRequest || !ReferenceEquals(root, Preview.Root)) return;
        var syntax = DesignerSyntax(source.Path);
        if (source.Version != syntax.Version)
        { _status = "Preview is stale · Run before editing its source"; return; }
        _selectedElement = syntax.Root?.DescendantsAndSelf().FirstOrDefault(element => element.Span.Start == source.Start && element.Span.Length == source.Length);
        if (_selectedElement == null) return;
        _selectedDesignerSyntax = syntax;
        // Keep keyboard focus in the preview during pointer gestures and Escape.
        await RevealDocumentAsync(source.Path, _selectedElement.Span, focus: false);
        if (_inspectorTab != "Designer")
        {
            await ShowPaneAsync("properties", focus: false);
        }
        if (request != _designerSelectionRequest || !ReferenceEquals(root, Preview.Root)) return;
        _status = "Selected " + _selectedElement.Name + " · " + source.Path + " · source revision " + source.Version;
        StateHasChanged();
    }
    private async Task CommitVisualEditsAsync(IReadOnlyList<XamlVisualEdit> edits)
    {
        if (_busy || _isolationVisible) throw new InvalidOperationException("The local designer is not active.");
        await _automationGate.WaitAsync();
        try
        {
            if (_busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
            await CaptureEditorsAsync();
            if (_busy || _disposed) throw new InvalidOperationException("The IDE operation was superseded.");
            CheckDesignerPreview();
            var snapshot = RuntimeInspector().Capture();
            foreach (var edit in edits)
                if (snapshot.Nodes.Count(node => node.IsSourceOwned && node.Source is { } source && source.Path == edit.Source.Path &&
                    source.Version == edit.Source.Version && source.Start == edit.Source.Start && source.Length == edit.Source.Length) != 1)
                    throw new InvalidOperationException("The visual source is missing or shared by multiple instances. Edit its XAML directly.");
            var changes = XamlBatchDesignerEdits.FromVisualEdits(DesignerDocuments(), edits);
            await CommitDesignerDocumentsAsync(changes, "Edit visual geometry");
        }
        finally { _automationGate.Release(); }
    }
    private async Task CommitDesignerTransactionAsync(XamlEditTransaction transaction)
    {
        var syntax = SelectedDesignerSyntax;
        if (_busy) return;
        await _automationGate.WaitAsync();
        try
        {
            if (_busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
            await CaptureEditorsAsync();
            if (_busy || _disposed) throw new InvalidOperationException("The IDE operation was superseded.");
            var current = DesignerSyntax(syntax.Path);
            if (current.Version != transaction.ExpectedRevision || current.Text != syntax.Text)
                throw new InvalidOperationException("The selected XAML source changed. Select the element again before editing.");
            await CommitDesignerDocumentsAsync([new(syntax.Path, syntax.Text, syntax.Version, transaction.Changes)], transaction.Description);
        }
        finally { _automationGate.Release(); }
    }
    private async Task CommitDesignerDocumentsAsync(ImmutableArray<XamlDocumentEdits> edits, string description)
    {
        if (edits.IsEmpty) return;
        RestoreWorkspace(_workspaceEdits.Apply(SourceRevision, edits, description, candidate => ValidateDesignerWorkspace(candidate.Documents)));
        await SaveDraftAsync(); await CompileSnapshotAsync();
        if (_result?.Success == true)
        {
            _busy = true;
            try
            {
                if (_isolationVisible) await ShowIsolatedCompilationAsync();
                else { _visualTree = await ShowTrustedCompilationAsync(_result); _previewShown = true; }
                _status = "Designer edit committed · one project undo step · reload " + Preview.Revision;
            }
            finally { _busy = false; }
        }
        StateHasChanged();
    }
    private void ValidateDesignerWorkspace(IReadOnlyDictionary<string, string> documents)
    {
        ValidateWorkspace(documents);
        var resources = documents.Where(pair => pair.Key != "View.axaml" && pair.Key != CompilerSettingsPath && !IsCSharpPath(pair.Key))
            .Select(pair => XamlSyntaxTree.Parse(pair.Value, pair.Key)).ToArray();
        var analysis = Compiler.Analyze(XamlSyntaxTree.Parse(documents["View.axaml"], "View.axaml"), documents["Code.cs"], resourceDocuments: resources, settings: ParseCompilerSettings(documents[CompilerSettingsPath]));
        if (!analysis.Success) throw new InvalidOperationException(string.Join("; ", analysis.Diagnostics.Where(diagnostic => diagnostic.Severity == "Error").Select(diagnostic => diagnostic.Message)));
    }
    private async Task<AvaloniaVisualNode> ShowTrustedCompilationAsync(BrowserCompilation result, bool activatePane = true)
    {
        if (activatePane) await ShowPaneAsync("preview", focus: false);
        if (!IsCompilationCurrent(result))
            throw new InvalidOperationException("Source changed after compilation. Run the current source again.");
        var revision = SourceRevision;
        return await Preview.ShowAsync(Compiler.Run(result), revision);
    }
    private void CheckDesignerPreview()
    {
        if (_isolationVisible || Preview.Root == null) throw new InvalidOperationException("Run the trusted preview before using visual design geometry.");
        if (Preview.SourceRevision != SourceRevision) throw new InvalidOperationException("The preview belongs to older project source. Reload it before planning geometry.");
    }
    private IEnumerable<XamlElementSyntax> ReparentDestinations => SelectedDesignerSyntax.Root?.DescendantsAndSelf()
        .Where(element => _selectedElement != null && !_selectedElement.Span.Contains(element.Span) && !element.LocalName.Contains('.')) ?? [];
    private async Task ReparentSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try
        {
            var destination = ReparentDestinations.FirstOrDefault(element => element.Span.Start == _reparentDestination)
                ?? throw new InvalidOperationException("Select a valid destination element.");
            await CommitDesignerTransactionAsync(XamlDesignerEdits.Reparent(SelectedDesignerSyntax, _selectedElement, destination));
        }
        catch (Exception error) { Report(error); }
    }
    private async Task InsertSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try { await CommitDesignerTransactionAsync(XamlDesignerEdits.InsertChild(SelectedDesignerSyntax, _selectedElement, _newElementMarkup)); }
        catch (Exception error) { Report(error); }
    }
    private async Task RemoveSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try { await CommitDesignerTransactionAsync(XamlDesignerEdits.RemoveElement(SelectedDesignerSyntax, _selectedElement)); }
        catch (Exception error) { Report(error); }
    }
}
