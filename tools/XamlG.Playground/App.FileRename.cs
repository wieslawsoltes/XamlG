using XamlG.Tooling;
using XamlG.Tooling.Editing;
using XamlG.Tooling.Refactoring;

namespace XamlG.Playground;

public partial class App
{
    private bool _fileMoveVisible;
    private bool _fileMoveWorking;
    private string _fileMoveSource = string.Empty;
    private string _fileMoveDestination = string.Empty;
    private string? _fileMoveError;
    private long _fileMoveRevision;
    private long _fileMoveGeneration;
    private BrowserCompilation? _fileMoveCompilation;
    private XamlFileRenamePlan? _fileMovePlan;

    private async Task OpenFileMoveAsync(string path)
    {
        if (!_ready || _busy || _fileMoveWorking) return;
        CloseAuthoring(); CloseFileMove();
        var generation = _fileMoveGeneration;
        _fileMoveWorking = true;
        try
        {
            await CaptureEditorsAsync();
            if (generation != _fileMoveGeneration) return;
            if (!Compiler.Resources.Snapshot.ContainsKey(path)) throw new InvalidOperationException("Select an existing project resource document.");
            var compilation = Compiler.Analyze(_document.Current, _code);
            if (!compilation.Success) throw new InvalidOperationException("Resolve compiler errors before moving a resource file.");
            _fileMoveCompilation = compilation;
            _fileMoveSource = _fileMoveDestination = path;
            _fileMoveRevision = _workspaceEdits.Current.Revision;
            _fileMoveVisible = true;
            _status = "Move resource · preview resolved source links before one atomic project edit";
        }
        catch (Exception error) { Report(error); }
        finally { if (generation == _fileMoveGeneration) _fileMoveWorking = false; }
    }
    private void FileMoveDestinationChanged(string path)
    {
        _fileMoveGeneration = checked(_fileMoveGeneration + 1);
        _fileMoveDestination = path; _fileMovePlan = null; _fileMoveError = null;
        _fileMoveWorking = false;
    }
    private async Task PreviewFileMoveAsync()
    {
        if (_fileMoveWorking || !_fileMoveVisible || _fileMoveCompilation == null) return;
        var generation = _fileMoveGeneration; var compilation = _fileMoveCompilation;
        _fileMovePlan = null; _fileMoveError = null; _fileMoveWorking = true;
        try
        {
            await CaptureEditorsAsync();
            if (generation != _fileMoveGeneration) return;
            if (_workspaceEdits.Current.Revision != _fileMoveRevision) throw new InvalidOperationException("Project source changed after this dialog opened. Cancel and start the move again.");
            var destination = XamlProjectDocumentStore.NormalizePath(_fileMoveDestination);
            if (destination == "View.axaml" || destination == _fileMoveSource)
                throw new InvalidOperationException("Choose a different resource path; View.axaml is owned by the studio.");
            var analyses = compilation.Project!.Documents.Select(d => new XamlAnalysis(d.Input.Syntax, d.Document, d.Output));
            _fileMovePlan = new XamlFileRenameService(compilation.AuthoringCompiler!).Plan(
                new[] { new XamlDocumentMove(_fileMoveSource, destination, destination) }, analyses);
        }
        catch (Exception error) { if (generation == _fileMoveGeneration) _fileMoveError = error.Message; }
        finally { if (generation == _fileMoveGeneration) _fileMoveWorking = false; }
    }
    private async Task ApplyFileMoveAsync()
    {
        if (_busy || _fileMoveWorking || _fileMovePlan == null) return;
        var generation = _fileMoveGeneration; var plan = _fileMovePlan;
        _fileMoveWorking = true;
        try
        {
            await CaptureEditorsAsync();
            if (generation != _fileMoveGeneration) return;
            if (_workspaceEdits.Current.Revision != _fileMoveRevision) throw new InvalidOperationException("Project source changed after preview. The move was not applied.");
            var snapshot = _workspaceEdits.ApplyFileRename(_fileMoveRevision, plan, candidate => ValidateWorkspace(candidate.Documents));
            // File identity, source and the selected editor change before the first await.
            RestoreWorkspace(snapshot, plan.Moves.Single().NewPath); CloseFileMove();
            var completedGeneration = _fileMoveGeneration;
            await SaveDraftAsync(); await CompileSnapshotAsync(); await RefreshAutomaticPreviewAsync();
            if (completedGeneration != _fileMoveGeneration) return;
            await OpenDocumentAsync(plan.Moves.Single().NewPath);
            _status = "Resource moved · linked sources updated · one project undo step";
        }
        catch (Exception error) { if (generation == _fileMoveGeneration) _fileMoveError = error.Message; else Report(error); }
        finally { if (generation == _fileMoveGeneration) _fileMoveWorking = false; }
    }
    private void CloseFileMove()
    {
        _fileMoveGeneration = checked(_fileMoveGeneration + 1);
        _fileMoveVisible = false; _fileMoveWorking = false; _fileMovePlan = null;
        _fileMoveCompilation = null; _fileMoveError = null;
    }
}
