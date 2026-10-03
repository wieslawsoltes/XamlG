using XamlG.Playground.Components;
using XamlG.Playground.Isolation;

namespace XamlG.Playground;

public partial class App
{
    private IsolatedPreview? _isolatedPreview;
    private bool _isolationVisible;
    private Task RunCurrentModeAsync() => _isolationVisible ? RunIsolatedAsync() : RunTrustedAsync();
    private async Task RunIsolatedAsync()
    {
        if (!_ready || _busy || _isolatedPreview == null) return;
        await CompileAsync();
        if (_result?.Success != true) return;
        _busy = true;
        try
        {
            _isolationVisible = true; _designMode = false; Preview.IsDesignMode = false;
            _status = "Starting isolated execution…";
            StateHasChanged(); await Task.Yield();
            await ShowIsolatedCompilationAsync();
        }
        catch (Exception error) { Report(error); }
        finally { _busy = false; }
    }
    private async Task ShowIsolatedCompilationAsync()
    {
        if (_result?.Success != true || _isolatedPreview == null) throw new InvalidOperationException("No valid isolated compilation is available.");
        var result = await _isolatedPreview.RunAsync(SandboxPayloadBuilder.Create(_result));
        _visualTree = result.Tree; _previewShown = true;
        _status = "Isolated preview running · opaque origin · reload " + result.Revision;
    }
    private async Task RunTrustedAsync()
    {
        if (_busy) return;
        _isolationVisible = false;
        await RunAsync();
    }
    private async Task ResetIsolationAsync()
    {
        if (_isolatedPreview != null) await _isolatedPreview.ResetAsync();
        _isolationVisible = false;
        _previewShown = Preview.Root != null;
        _visualTree = Preview.Inspect();
        _status = "Isolated execution frame discarded";
    }
}
