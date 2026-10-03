using XamlG.Runtime;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private bool _designMode;
    private int _reparentDestination = -1;
    private string _newElementMarkup = "<Button Content=\"New button\" />";

    protected override void OnInitialized()
    {
        Preview.SourceSelected = source => InvokeAsync(() => SelectVisualSourceAsync(source));
        Preview.EditRequested = edit => InvokeAsync(() => CommitVisualEditAsync(edit));
        Preview.Error = error => { Report(error); _ = InvokeAsync(StateHasChanged); };
    }
    private void ToggleDesignMode()
    {
        if (_isolationVisible) return;
        _designMode = !_designMode; Preview.IsDesignMode = _designMode;
        _status = _designMode ? "Design mode · drag to move · eight resize handles · Shift locks aspect · Alt disables snapping · Escape cancels" : "Interaction mode";
    }
    private async Task SelectVisualSourceAsync(XamlSourceInfo source)
    {
        if (_isolationVisible || source.Version != _document.Current.Version || source.Path != _document.Current.Path)
        { _status = "Preview is stale · Run before editing its source"; return; }
        _selectedElement = _document.Current.Root?.DescendantsAndSelf().FirstOrDefault(e => e.Span.Start == source.Start && e.Span.Length == source.Length);
        if (_selectedElement == null) return;
        _editorTab = "xaml"; _inspectorTab = "Properties";
        if (_xamlEditor != null) await _xamlEditor.RevealAsync(_selectedElement.Span);
        _status = "Selected " + _selectedElement.Name + " · source revision " + source.Version;
        StateHasChanged();
    }
    private async Task CommitVisualEditAsync(XamlVisualEdit edit)
    {
        if (_busy || _isolationVisible) throw new InvalidOperationException("The local designer is not active.");
        await CaptureEditorsAsync();
        await CommitDesignerTransactionAsync(XamlBatchDesignerEdits.FromVisualEdit(_document.Current, edit));
    }
    private async Task CommitDesignerTransactionAsync(XamlEditTransaction transaction)
    {
        var candidate = _document.Current.WithChanges(transaction.Changes, transaction.ExpectedRevision);
        var analysis = Compiler.Analyze(candidate, _code);
        if (!analysis.Success)
            throw new InvalidOperationException(string.Join("; ", analysis.Diagnostics.Where(d => d.Severity == "Error").Select(d => d.Message)));
        _document.Apply(transaction, requireWellFormed: true);
        _selectedElement = null;
        await SaveDraftAsync();
        await CompileSnapshotAsync();
        if (_result?.Success == true)
        {
            if (_isolationVisible) await ShowIsolatedCompilationAsync();
            else
            {
                _visualTree = await Preview.ShowAsync(Compiler.Run(_result));
                _previewShown = true;
                _status = "Designer edit committed · state-preserving reload " + Preview.Revision;
            }
        }
        StateHasChanged();
    }
    private IEnumerable<XamlElementSyntax> ReparentDestinations => _document.Current.Root?.DescendantsAndSelf()
        .Where(e => _selectedElement != null && !_selectedElement.Span.Contains(e.Span) && !e.LocalName.Contains('.')) ?? Enumerable.Empty<XamlElementSyntax>();
    private async Task ReparentSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try
        {
            var destination = ReparentDestinations.FirstOrDefault(e => e.Span.Start == _reparentDestination)
                ?? throw new InvalidOperationException("Select a valid destination element.");
            await CommitDesignerTransactionAsync(XamlDesignerEdits.Reparent(_document.Current, _selectedElement, destination));
        }
        catch (Exception error) { Report(error); }
    }
    private async Task InsertSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try { await CommitDesignerTransactionAsync(XamlDesignerEdits.InsertChild(_document.Current, _selectedElement, _newElementMarkup)); }
        catch (Exception error) { Report(error); }
    }
    private async Task RemoveSelectedAsync()
    {
        if (_selectedElement == null || _busy) return;
        try { await CommitDesignerTransactionAsync(XamlDesignerEdits.RemoveElement(_document.Current, _selectedElement)); }
        catch (Exception error) { Report(error); }
    }
}
