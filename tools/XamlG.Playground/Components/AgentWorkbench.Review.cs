using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground.Components;

public partial class AgentWorkbench
{
    private ElementReference _threadElement;
    private string? _handoff;
    private bool _handoffBusy;
    private string _reviewFeedback = "";
    private object? _feedbackTarget;
    private DiffView[] _diffPreview = [];
    private RestoreReview? _restoreReview;
    private static string? SourceExcerpt(string? text) => text is { Length: > 20000 } ? text[..20000] + "\n[display excerpt; export the patch for complete source]" : text;

    private async Task CopyThreadAsync()
    {
        try { await CopyTextAsync(await RequestAsync<string>("export_markdown", new { id = _selectedId })); }
        catch (JSException error) { _error = error.Message; }
    }
    private async Task CopyTextAsync(string text)
    { try { await _module!.InvokeVoidAsync("copyAgentText", text); } catch (JSException error) { _error = error.Message; } }
    private async Task FollowThreadAsync() => await _module!.InvokeVoidAsync("followAgentThread", _threadElement);
    private async Task BeginHandoffAsync()
    { try { _handoff = await RequestAsync<string>("handoff", new { id = _selectedId }); } catch (JSException error) { _error = error.Message; } }
    private async Task CreateWithContextAsync()
    {
        if (_handoff == null || _handoffBusy) return;
        _handoffBusy = true;
        try
        {
            var text = _handoff;
            var task = await RequestAsync<TaskView>("create", new { name = _name, provider = _provider, model = _model, accountId = _provider == ChatGptProvider ? ActiveAccount?.Id : null });
            await RequestAsync<JsonElement>("draft", new { id = task.Id, text });
            _handoff = null; await RefreshAsync(); Select(task.Id); _draft = text;
        }
        catch (JSException error) { _error = error.Message; }
        finally { _handoffBusy = false; }
    }
    private async Task PreviewDiffAsync()
    {
        try { _diffPreview = await RequestAsync<DiffView[]>("diff", new { id = _selectedId, latestRun = _latestRun }); _changePreview = []; }
        catch (JSException error) { _error = error.Message; }
    }
    private void SelectDiffLine(string path, DiffLineView line)
    { _feedbackTarget = new { path, line.BeforeLine, line.AfterLine, line.Kind, line.Text, revision = SelectedChanges?.Revision, baseline = _latestRun ? "latest run" : "task start" }; }
    private async Task QueueReviewFeedbackAsync()
    {
        var text = "Review feedback for the captured source comparison (re-read the current project before editing):\n" +
            JsonSerializer.Serialize(new { baseline = _latestRun ? "latest run" : "task start", revision = SelectedChanges?.Revision, target = _feedbackTarget, feedback = _reviewFeedback });
        if (await ChangeQueueAsync("queue", new { id = _selectedId, text })) { _reviewFeedback = ""; _feedbackTarget = null; }
    }
    private async Task ExportPatchAsync()
    {
        try
        {
            var patch = await RequestAsync<string>("patch", new { id = _selectedId, latestRun = _latestRun });
            await _module!.InvokeVoidAsync("download", "xamlg-agent-review.patch", patch, "text/plain");
        }
        catch (JSException error) { _error = error.Message; }
    }
    private void ReviewRestore() => _restoreReview = new(_selectedId, _restorePaths.ToArray(), SelectedChanges!.Revision, _latestRun);
    private async Task ConfirmRestoreAsync()
    {
        if (_restoreReview is not { } review) return;
        try
        {
            await RequestAsync<JsonElement>("restore", new { id = review.Id, paths = review.Paths, expectedRevision = review.Revision, latestRun = review.LatestRun });
            _restoreReview = null; _restorePaths.Clear(); _diffPreview = []; _changePreview = []; await RefreshAsync();
        }
        catch (JSException error) { _error = error.Message; }
    }
    private sealed record RestoreReview(string Id, string[] Paths, long Revision, bool LatestRun);
    public sealed class DiffView { public string Path { get; set; } = ""; public DiffLineView[] Lines { get; set; } = []; public bool Coarse { get; set; } public bool Truncated { get; set; } }
    public sealed class DiffLineView { public string Kind { get; set; } = ""; public int? BeforeLine { get; set; } public int? AfterLine { get; set; } public string Text { get; set; } = ""; }
}
