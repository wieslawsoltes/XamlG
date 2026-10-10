using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Playground.Git;

namespace XamlG.Playground;

public partial class App
{
    private readonly Dictionary<string, GitDocumentRequest> _gitDocuments = new(StringComparer.Ordinal);

    private async Task OpenGitDocumentAsync(GitDocumentRequest request)
    {
        if (_disposed || !_dockReady || _module is null) return;
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Id);
        ArgumentNullException.ThrowIfNull(request.Title);
        if (!request.Id.StartsWith("gitdoc:", StringComparison.Ordinal) || request.Id.Length > 100 || request.Title.Length > 4096)
            throw new ArgumentException("Invalid Git document identity.");
        _gitDocuments[request.Id] = request;
        if ((await DockContentsAsync()).All(pane => pane.Id != request.Id))
        {
            await using var pane = await _dock.AddDocumentAsync(PaneContent(request.Id, request.Title, "git-document"));
        }
        await _module.InvokeVoidAsync("activateDockContent", _dockManager, request.Id, true);
        StateHasChanged();
    }

    private async Task ImportGitSourceAsync(GitSourceRequest request)
    {
        if (_disposed || !_ready || _busy) throw new InvalidOperationException("Studio is not ready for a source import.");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)) ||
            !(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) ||
            request.Text.Length > 16 * 1024 * 1024)
            throw new ArgumentException("Import a bounded, repository-relative C# or XAML source path.");

        // Cancel queued execution before the first asynchronous boundary.
        CancelAutomaticUpdate();
        await GitHostTransactionAsync(async () =>
        {
            // Import never inherits the owner's automatic execution policy.
            await AutoCompileChangedAsync(new ChangeEventArgs { Value = false });
            await AutoPreviewChangedAsync(new ChangeEventArgs { Value = false });
            await CaptureEditorsAsync();
            RecordWorkspace();
            var texts = WorkspaceTexts();
            texts[path] = request.Text;
            _workspaceEdits.ReplaceAll(_workspaceEdits.Current.Revision, texts, $"Import Git source: {path}");
            RestoreWorkspace(_workspaceEdits.Current);
            await SaveDraftAsync();
            await OpenDocumentAsync(path);
            _status = "Git source imported · automatic compile and preview disabled";
            return true;
        });
    }

    private Task<string> ReadStudioSourceAsync(string path) => GitHostTransactionAsync(async () =>
    {
        await CaptureEditorsAsync();
        return WorkspaceTexts().TryGetValue(path, out var text)
            ? text : throw new InvalidOperationException("The selected Studio source document does not exist.");
    });

    // Git-to-Studio transfers share the same exclusion boundary as agent edits and
    // automatic compilation. UI commands also observe _busy while an import yields.
    private async Task<T> GitHostTransactionAsync<T>(Func<Task<T>> action)
    {
        await _automationGate.WaitAsync();
        try
        {
            if (_disposed || !_ready || _busy)
                throw new InvalidOperationException("Studio is not ready for a Git source transfer.");
            _busy = true;
            try { return await action(); }
            finally
            {
                _busy = false;
                if (!_disposed) StateHasChanged();
            }
        }
        finally { _automationGate.Release(); }
    }

    private async Task DisposeGitAsync()
    {
        try
        {
            var shared = await JavaScript.InvokeAsync<IJSObjectReference>("xamlgBoot.importModule", "git/workbench.mjs");
            await using var interop = await shared.InvokeAsync<IJSObjectReference>("createWorkbenchInterop");
            await interop.InvokeVoidAsync("retireSession", _panePrefix);
        }
        // Asset/network failures must not prevent disposal of the remaining Studio services.
        catch (JSException) { }
    }
}
