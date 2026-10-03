using XamlG.Playground.Components;
using XamlG.Playground.Isolation;

namespace XamlG.Playground;

public partial class App
{
    private IsolatedPreview? _isolatedPreview;
    private bool _isolationVisible;
    private long _isolationGeneration;
    private Task RunCurrentModeAsync() => _isolationVisible ? RunIsolatedAsync() : RunTrustedAsync();
    private async Task RunIsolatedAsync()
    {
        if (!_ready || _busy || _isolatedPreview == null) return;
        var generation = _isolationGeneration;
        await CompileAsync();
        if (_result?.Success != true || generation != _isolationGeneration) return;
        _busy = true;
        try
        {
            _isolationVisible = true; _designMode = false; Preview.IsDesignMode = false;
            _status = "Starting isolated execution…";
            StateHasChanged(); await Task.Yield();
            await ShowIsolatedCompilationAsync();
        }
        catch (Exception error) when (generation == _isolationGeneration) { Report(error); }
        catch (Exception) when (generation != _isolationGeneration) { /* Reset owns the new UI state. */ }
        finally { if (generation == _isolationGeneration) _busy = false; }
    }
    private async Task ShowIsolatedCompilationAsync()
    {
        if (_result?.Success != true || _isolatedPreview == null) throw new InvalidOperationException("No valid isolated compilation is available.");
        var generation = _isolationGeneration;
        var result = await _isolatedPreview.RunAsync(SandboxPayloadBuilder.Create(_result));
        if (generation != _isolationGeneration || !_isolationVisible) throw new OperationCanceledException("The isolated execution result was superseded.");
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
        _isolationGeneration = checked(_isolationGeneration + 1);
        _isolationVisible = false;
        _busy = false;
        if (_isolatedPreview != null) await _isolatedPreview.ResetAsync();
        _previewShown = Preview.Root != null;
        _visualTree = Preview.Inspect();
        _status = "Isolated execution frame discarded";
    }
}
