using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Playground.Components;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    private readonly Dictionary<string, DocumentBuffer> _documentBuffers = new(StringComparer.Ordinal);
    private string _activeDocumentPath = "View.axaml";
    private bool _documentsNeedReconcile;
    private static string DocumentId(string path, bool generated = false) => (generated ? "generated:" : "document:") + Uri.EscapeDataString(path);
    private CodeEditor? DocumentEditor(string path, bool generated = false) =>
        _documentBuffers.GetValueOrDefault(DocumentId(path, generated))?.Editor is { IsRetired: false } editor ? editor : null;
    private IEnumerable<string> SourceDocumentPaths => WorkspaceTexts().Keys.Where(path => path != CompilerSettingsPath).Order(StringComparer.Ordinal);

    private DocumentBuffer? DocumentForId(string id)
    {
        ReconcileSourceBuffers();
        return _documentBuffers.GetValueOrDefault(id);
    }
    private void ReconcileSourceBuffers()
    {
        var current = WorkspaceTexts();
        foreach (var buffer in _documentBuffers.Values.Where(buffer => !buffer.Generated).ToArray())
            if (!current.TryGetValue(buffer.Path, out var text) || text != buffer.Text)
            { buffer.Retired = true; _documentBuffers.Remove(buffer.Id); _documentsNeedReconcile = true; }
        foreach (var (path, text) in current.Where(pair => pair.Key != CompilerSettingsPath))
        {
            var id = DocumentId(path);
            if (!_documentBuffers.ContainsKey(id)) _documentBuffers.Add(id, new(id, path, text, false, SourceRevision));
        }
    }
    private void ReconcileGeneratedBuffers()
    {
        if (_result == null || !IsCompilationCurrent(_result)) return;
        var current = _result.Compilation.SyntaxTrees.Where(tree => !_result.SourcePaths.Contains(tree.FilePath))
            .ToDictionary(tree => tree.FilePath, tree => tree.ToString(), StringComparer.Ordinal);
        foreach (var buffer in _documentBuffers.Values.Where(buffer => buffer.Generated).ToArray())
            if (!current.TryGetValue(buffer.Path, out var text) || text != buffer.Text)
            { buffer.Retired = true; _documentBuffers.Remove(buffer.Id); _documentsNeedReconcile = true; }
        foreach (var (path, text) in current)
        {
            var id = DocumentId(path, true);
            if (!_documentBuffers.TryGetValue(id, out var buffer)) _documentBuffers.Add(id, new(id, path, text, true, SourceRevision));
            else buffer.GeneratedRevision = SourceRevision;
        }
    }
    private void RetireDocumentBuffers()
    {
        foreach (var buffer in _documentBuffers.Values) buffer.Retired = true;
        _documentBuffers.Clear(); _documentsNeedReconcile = true;
    }
    private bool OwnsDocument(DocumentBuffer buffer) => !buffer.Retired &&
        _documentBuffers.TryGetValue(buffer.Id, out var current) && ReferenceEquals(current, buffer) &&
        WorkspaceTexts().GetValueOrDefault(buffer.Path) == buffer.Text;
    private void ApplyDocumentText(DocumentBuffer buffer, string text)
    {
        if (buffer.Generated || !OwnsDocument(buffer) || text == buffer.Text) return;
        switch (buffer.Path)
        {
            case "View.axaml": UpdateXaml(text); break;
            case "Code.cs": _code = text; break;
            default:
                if (IsCSharpPath(buffer.Path))
                { var source = Compiler.CodeFiles.Snapshot[buffer.Path]; Compiler.CodeFiles.Update(buffer.Path, source.Version, text); }
                else
                { var source = Compiler.Resources.Snapshot[buffer.Path]; Compiler.Resources.Update(buffer.Path, source.Version, text); }
                break;
        }
        buffer.Text = text;
        _status = "Source changed · compile to update inspections";
    }
    private async Task DocumentChangedAsync(DocumentBuffer buffer, string text)
    {
        if (!OwnsDocument(buffer)) return;
        ApplyDocumentText(buffer, text); await SaveDraftAsync();
        ScheduleAutomaticUpdate();
    }
    private async Task CaptureDocumentBuffersAsync()
    {
        foreach (var buffer in _documentBuffers.Values.Where(buffer => !buffer.Generated).ToArray())
        {
            var editor = buffer.Editor;
            if (editor is not { IsRetired: false } || !OwnsDocument(buffer)) continue;
            var text = await editor.TryGetTextAsync();
            if (text != null && ReferenceEquals(editor, buffer.Editor) && !editor.IsRetired) ApplyDocumentText(buffer, text);
        }
    }
    private async Task OpenDocumentAsync(string path, bool generated = false, bool focus = true)
    {
        if (!_dockReady) return;
        ReconcileSourceBuffers(); ReconcileGeneratedBuffers();
        var id = DocumentId(path, generated);
        if (!_documentBuffers.TryGetValue(id, out var buffer)) throw new InvalidOperationException("This source document is unavailable.");
        await EnsureDocumentPaneAsync(buffer, focus);
        _activeDocumentPath = path;
        if (!generated) _editorTab = path == "Code.cs" ? "code" : "xaml";
        StateHasChanged();
        await RevealDocumentBuffersAsync();
    }
    private async Task RevealDocumentAsync(string path, TextSpan span, bool focus = true, bool generated = false)
    {
        await OpenDocumentAsync(path, generated, focus);
        if (_documentBuffers.TryGetValue(DocumentId(path, generated), out var buffer))
        { buffer.PendingReveal = (span, focus); await RevealDocumentBuffersAsync(); }
    }
    private async Task RevealDocumentBuffersAsync()
    {
        foreach (var buffer in _documentBuffers.Values.ToArray())
            if (!buffer.Retired && buffer.PendingReveal is { } reveal && buffer.Editor is { IsRetired: false } editor)
            { buffer.PendingReveal = null; await editor.RevealAsync(reveal.Span, reveal.Focus); }
    }
    private async Task DocumentReadyAsync(DocumentBuffer buffer)
    {
        if (buffer.Retired || !_documentBuffers.TryGetValue(buffer.Id, out var current) || !ReferenceEquals(buffer, current)) return;
        await RevealDocumentBuffersAsync();
        if (_result != null && buffer.Editor != null)
            await buffer.Editor.SetDiagnosticsAsync(_result.Diagnostics.Where(d => !d.IsSuppressed && d.Path == buffer.Path));
    }
    private Task DocumentCommandAsync(string path, string command) => DocumentEditor(path)?.RequestCommandAsync(command) ?? Task.CompletedTask;

    [JSInvokable]
    public async Task<bool> PrepareDockContentClose(string id)
    {
        if (_disposed || _busy) return false;
        try
        {
            if (id.StartsWith("document:", StringComparison.Ordinal)) await CaptureEditorsAsync();
            if (id == "agent" && _agentWorkbench != null) await _agentWorkbench.ClosePanelAsync();
            return !_disposed;
        }
        catch (Exception error) { Report(error); StateHasChanged(); return false; }
    }
    private sealed class DocumentBuffer(string id, string path, string text, bool generated, long revision)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public string Text { get; set; } = text;
        public bool Generated { get; } = generated;
        public long GeneratedRevision { get; set; } = revision;
        public CodeEditor? Editor;
        public bool Retired;
        public (TextSpan Span, bool Focus)? PendingReveal;
    }
}
