using System.Collections.Immutable;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Editing;
using XamlG.Tooling.Formatting;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private readonly XamlWorkspaceEditSession _workspaceEdits = new(new Dictionary<string, string>
    { ["View.axaml"] = PlaygroundExamples.All[0].Xaml, ["Code.cs"] = PlaygroundExamples.All[0].Code, [CompilerSettingsPath] = DefaultCompilerSettingsText });
    private EditorCommandRequest? _authoringRequest;
    private long _authoringRevision;
    private XamlAnalysis? _authoringAnalysis;
    private XamlRenamePlan? _renamePlan;
    private ImmutableArray<XamlCodeAction> _codeActions = ImmutableArray<XamlCodeAction>.Empty;
    private string _renameName = string.Empty;
    private string _renameOriginal = string.Empty;
    private string? _authoringError;
    private bool _renameVisible;
    private bool _actionsVisible;

    private Dictionary<string, string> WorkspaceTexts()
    {
        var sources = ResourceTexts();
        foreach (var item in CodeTexts()) sources.Add(item.Key, item.Value);
        sources.Add("View.axaml", _document.Current.Text); sources.Add("Code.cs", _code);
        sources.Add(CompilerSettingsPath, _compilerSettingsText);
        return sources;
    }
    private void RecordWorkspace(string description = "Edit project source")
    {
        var previous = _workspaceEdits.Current.Revision;
        _workspaceEdits.ReplaceAll(previous, WorkspaceTexts(), description);
        if (_workspaceEdits.Current.Revision != _resourceSourceRevision) NotifySourceResources();
    }
    private void ResetWorkspaceHistory()
    {
        _workspaceEdits.ReplaceAll(_workspaceEdits.Current.Revision, WorkspaceTexts(), "Load project", false);
        _workspaceEdits.ClearHistory(); CloseAuthoring(); ReconcileSourceBuffers();
    }
    private void RestoreWorkspace(XamlWorkspaceSnapshot snapshot, string? preferredResourcePath = null)
    {
        var settingsText = snapshot.Documents[CompilerSettingsPath];
        Compiler.SetSettings(ParseCompilerSettings(settingsText)); _compilerSettingsText = settingsText;
        // Keep the main syntax revision monotonic so an old realized visual cannot target a new buffer.
        UpdateXaml(snapshot.Documents["View.axaml"]); _code = snapshot.Documents["Code.cs"];
        var resources = snapshot.Documents.Where(p => p.Key != "View.axaml" && p.Key != "Code.cs" && p.Key != CompilerSettingsPath)
            .Where(p => !IsCSharpPath(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var current = Compiler.Resources.Snapshot;
        if (resources.Count != current.Count || resources.Any(p => !current.TryGetValue(p.Key, out var value) || value.Text != p.Value))
            Compiler.Resources.ReplaceAll(resources);
        // Reconcile identity and editor ownership before SaveDraft/compilation can yield.
        // Otherwise an old resource editor could be read into a renamed/restored document.
        _resourceEditor?.SynchronizeDocuments(preferredResourcePath);
        var code = snapshot.Documents.Where(p => p.Key != "Code.cs" && IsCSharpPath(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var currentCode = Compiler.CodeFiles.Snapshot;
        if (code.Count != currentCode.Count || code.Any(p => !currentCode.TryGetValue(p.Key, out var value) || value.Text != p.Value))
            Compiler.CodeFiles.ReplaceAll(code);
        _projectCodeEditor?.SynchronizeDocuments();
        ReconcileSourceBuffers();
        _selectedElement = null; _selectedDesignerSyntax = null; _selectedVisual = null; _result = null;
    }
    private async Task NavigateWorkspaceAsync(bool undo)
    {
        if (_busy) return;
        await CaptureEditorsAsync();
        CloseAuthoring();
        var snapshot = undo ? _workspaceEdits.Undo(_workspaceEdits.Current.Revision) : _workspaceEdits.Redo(_workspaceEdits.Current.Revision);
        RestoreWorkspace(snapshot);
        await SaveDraftAsync(); await CompileSnapshotAsync();
    }
    private Task RequestMainCommandAsync(string command) => DocumentCommandAsync(_activeDocumentPath, command);
    private async Task AuthoringCommandAsync(EditorCommandRequest request)
    {
        if (_busy || !_ready) return;
        if (request.Command == "undo") { await NavigateWorkspaceAsync(true); return; }
        if (request.Command == "redo") { await NavigateWorkspaceAsync(false); return; }
        CloseAuthoring();
        try
        {
            await CaptureEditorsAsync();
            var snapshot = _workspaceEdits.Current;
            if (!snapshot.Documents.TryGetValue(request.Path, out var text) || text != request.Text)
                throw new InvalidOperationException("The command's source buffer changed; invoke it again on the current text.");
            if (request.Start < 0 || request.Length < 0 || request.Start > text.Length || request.Length > text.Length - request.Start)
                throw new InvalidOperationException("The editor selection is outside its source snapshot.");
            if (IsCSharpPath(request.Path)) { await CSharpAuthoringCommandAsync(request); return; }
            _result = Compiler.Analyze(_document.Current, _code);
            var item = _result.Project!.Documents.Single(d => d.Input.LogicalPath == request.Path);
            _authoringAnalysis = new(item.Input.Syntax, item.Document, item.Output);
            _authoringRequest = request; _authoringRevision = snapshot.Revision;
            switch (request.Command)
            {
                case "rename":
                    var name = ProjectRename().Prepare(request.Path, request.Start)
                        ?? throw new InvalidOperationException("Select an x:Name or a resolved CLR type, namespace or member reference.");
                    _renameName = _renameOriginal = name.Name; _renameVisible = true;
                    _status = "Rename previews all XAML and C# changes before one atomic project edit";
                    break;
                case "format":
                    var edits = XamlFormatter.Format(item.Input.Syntax, new() { TabSize = 2 },
                        request.Length == 0 ? null : new TextSpan(request.Start, request.Length), _authoringAnalysis);
                    await ApplyAuthoringEditsAsync(ImmutableArray.Create(new XamlDocumentEdits(request.Path, text, item.Input.Syntax.Version, edits)), "Format XAML");
                    break;
                case "actions":
                    _codeActions = new XamlCodeActionService(_result.AuthoringCompiler!).GetActions(_authoringAnalysis,
                        new(request.Start, request.Length), new() { TabSize = 2 });
                    _actionsVisible = true; break;
                default: throw new ArgumentException("Unknown authoring command: " + request.Command);
            }
        }
        catch (Exception error) { Report(error); }
    }
    private async Task PreviewRenameAsync()
    {
        _authoringError = null; _renamePlan = null;
        try
        {
            await CaptureEditorsAsync();
            if (_workspaceEdits.Current.Revision != _authoringRevision || _authoringRequest == null)
                throw new InvalidOperationException("Project source changed after rename opened. Cancel and invoke Rename again.");
            if (IsCSharpPath(_authoringRequest.Path))
            { _renamePlan = PlanCSharpRename(_authoringRequest.Path, _authoringRequest.Start, _renameName); return; }
            if (_authoringAnalysis == null) throw new InvalidOperationException("The XAML authoring snapshot is no longer available.");
            _renamePlan = ProjectRename().Rename(_authoringRequest.Path, _authoringRequest.Start, _renameName);
        }
        catch (Exception error) { _authoringError = error.Message; }
    }
    private async Task ApplyRenameAsync()
    {
        if (_renamePlan == null) return;
        try { await ApplyAuthoringEditsAsync(_renamePlan.Documents, "Rename " + _renamePlan.OldName + " to " + _renamePlan.NewName); }
        catch (Exception error) { _authoringError = error.Message; }
    }
    private async Task ApplyCodeActionAsync(XamlCodeAction action)
    {
        if (_authoringRequest is { } request && IsCSharpPath(request.Path))
        {
            try { await ApplyAuthoringEditsAsync([new(request.Path, request.Text, null, action.Changes)], action.Title); }
            catch (Exception error) { _authoringError = error.Message; }
            return;
        }
        if (_authoringAnalysis == null) return;
        var syntax = _authoringAnalysis.Syntax;
        try { await ApplyAuthoringEditsAsync(ImmutableArray.Create(new XamlDocumentEdits(syntax.Path, syntax.Text, syntax.Version, action.Changes)), action.Title); }
        catch (Exception error) { _authoringError = error.Message; }
    }
    private async Task ApplyAuthoringEditsAsync(ImmutableArray<XamlDocumentEdits> edits, string description)
    {
        if (_busy) return;
        // Flush all editors again: a rename preview is not permission to overwrite subsequent typing.
        await CaptureEditorsAsync();
        var snapshot = _workspaceEdits.Apply(_authoringRevision, edits, description, candidate => ValidateWorkspace(candidate.Documents));
        RestoreWorkspace(snapshot); CloseAuthoring();
        await SaveDraftAsync(); await CompileSnapshotAsync();
        _status = description + " · " + edits.Count(e => e.Changes.Length != 0) + " documents · one project undo step";
    }
    private void RenameNameChanged(string value) { _renameName = value; _renamePlan = null; _authoringError = null; }
    private void CloseAuthoring()
    {
        _renameVisible = false; _actionsVisible = false; _authoringRequest = null;
        _authoringAnalysis = null; _renamePlan = null; _codeActions = ImmutableArray<XamlCodeAction>.Empty; _authoringError = null;
    }
}
