using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using XamlG.Playground.Editing;
using XamlG.Syntax;

namespace XamlG.Playground.Components;

/// <summary>Component lifetime and editor-buffer lifetime are explicit. No JS reference owned
/// by another component is disposed here, and late reads cannot revive a retired component.</summary>
public partial class CodeEditor : ComponentBase, IAsyncDisposable
{
    [Inject] public EditorInteropModule Editors { get; set; } = default!;
    [Parameter] public string Text { get; set; } = string.Empty;
    [Parameter] public string? DocumentPath { get; set; }
    [Parameter] public EventCallback<EditorCommandRequest> AuthoringRequested { get; set; }
    [Parameter] public string Language { get; set; } = "xml";
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public EventCallback<string> TextChanged { get; set; }
    [Parameter] public EventCallback RunRequested { get; set; }

    private ElementReference _host = default;
    private EditorInteropSession? _session;
    private string? _parameterText;
    private bool _retired;
    private Task? _disposal;

    public bool IsRetired => _retired;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _retired) return;
        var callback = DotNetObjectReference.Create(this);
        EditorInteropSession session;
        try
        {
            session = Editors.Create(new object?[] { _host, callback, Text, Language, ReadOnly, DocumentPath }, callback);
        }
        catch { callback.Dispose(); throw; }
        _session = session;
        var initialText = Text;
        try
        {
            await session.Ready;
            if (_retired || !ReferenceEquals(session, _session)) return;
            // A parameter replacement may arrive while Monaco assets are loading.
            var current = _parameterText ?? Text;
            if (current != initialText) await session.InvokeAsync("setEditorText", current);
        }
        catch
        {
            if (ReferenceEquals(_session, session)) _session = null;
            await session.DisposeAsync();
            if (!_retired) throw;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_retired || _parameterText == Text) return;
        _parameterText = Text;
        if (_session is { } session) await session.InvokeAsync("setEditorText", Text);
    }

    [JSInvokable]
    public Task Changed(string text)
    {
        if (_retired) return Task.CompletedTask;
        _parameterText = text;
        return TextChanged.InvokeAsync(text);
    }
    [JSInvokable] public Task Run() => _retired ? Task.CompletedTask : RunRequested.InvokeAsync();
    [JSInvokable] public Task Authoring(EditorCommandRequest request) =>
        _retired ? Task.CompletedTask : AuthoringRequested.InvokeAsync(request);

    public async Task RequestCommandAsync(string command)
    {
        if (_retired || _session is not { } session) return;
        // Read data under the editor lease, then invoke managed callbacks outside its gate.
        // Awaiting requestAuthoring's callback under that gate would deadlock recursive Capture.
        var request = await session.ReadAsync<EditorCommandRequest>("getAuthoringRequest", command);
        if (!_retired && ReferenceEquals(session, _session) && request != null)
            await AuthoringRequested.InvokeAsync(request);
    }

    public async Task<string?> TryGetTextAsync()
    {
        if (_retired) return null;
        var session = _session;
        // Before first render no user-editable JS buffer exists. Never use this fallback after retirement.
        if (session == null) return Text;
        var text = await session.ReadAsync<string>("getEditorText");
        if (_retired || !ReferenceEquals(session, _session) || text == null) return null;
        _parameterText = text;
        return text;
    }

    public async Task<string> GetTextAsync() => await TryGetTextAsync()
        ?? throw new ObjectDisposedException(nameof(CodeEditor), "The source editor retired during capture.");

    public Task RevealAsync(TextSpan span) => _retired || _session == null ? Task.CompletedTask :
        _session.InvokeAsync("reveal", span.Start, span.Length);

    public Task SetDiagnosticsAsync(IEnumerable<PlaygroundDiagnostic> diagnostics) => _retired || _session == null ? Task.CompletedTask :
        _session.InvokeAsync("setMarkers", diagnostics);

    public ValueTask DisposeAsync()
    {
        if (_disposal != null) return new(_disposal);
        // Publish retirement and detach the lease before the first cleanup await.
        _retired = true;
        var session = _session;
        _session = null;
        _disposal = session?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        return new(_disposal);
    }
}
