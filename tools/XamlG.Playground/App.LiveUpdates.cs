using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace XamlG.Playground;

public partial class App
{
    private bool _autoCompile = true, _autoPreview = true;
    private CancellationTokenSource? _automaticUpdate;

    private async Task LoadLiveUpdatesAsync()
    {
        var preferences = await _module!.InvokeAsync<LiveUpdatePreferences>("loadLiveUpdates");
        _autoCompile = preferences.Compile; _autoPreview = preferences.Preview;
    }

    private async Task AutoCompileChangedAsync(ChangeEventArgs args)
    {
        _autoCompile = args.Value is true;
        await SaveLiveUpdatesAsync();
        ScheduleAutomaticUpdate();
        if (!_autoCompile) _status = "Automatic compilation paused · use Compile or Run preview";
    }

    private async Task AutoPreviewChangedAsync(ChangeEventArgs args)
    {
        _autoPreview = args.Value is true;
        await SaveLiveUpdatesAsync();
        ScheduleAutomaticUpdate();
    }

    private async Task SaveLiveUpdatesAsync()
    {
        if (_module != null) await _module.InvokeVoidAsync("saveLiveUpdates", _autoCompile, _autoPreview);
    }

    private void CancelAutomaticUpdate()
    {
        var previous = _automaticUpdate;
        _automaticUpdate = null;
        previous?.Cancel();
    }

    // Only owner edits schedule execution. MCP and agent source edits keep their
    // separate runtime permission checks and invalidate any queued owner update.
    private void ScheduleAutomaticUpdate()
    {
        CancelAutomaticUpdate();
        if (!_autoCompile || !_ready || _disposed) return;
        var cancellation = _automaticUpdate = new();
        _status = "Source changed · automatic compilation scheduled";
        _ = AutomaticUpdateAsync(cancellation, SourceRevision);
    }

    private async Task AutomaticUpdateAsync(CancellationTokenSource cancellation, long revision)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(600, token);
            while (_busy) await Task.Delay(100, token);
            await _automationGate.WaitAsync(token);
            try
            {
                if (_disposed || !_autoCompile || SourceRevision != revision || cancellation != _automaticUpdate) return;
                await CompileSnapshotAsync(captureEditors: true);
                if (token.IsCancellationRequested || _disposed || SourceRevision != revision || cancellation != _automaticUpdate) return;
                await RefreshAutomaticPreviewAsync();
            }
            finally { _automationGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) Report(error); }
        finally
        {
            if (cancellation == _automaticUpdate) _automaticUpdate = null;
            cancellation.Dispose();
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task RefreshAutomaticPreviewAsync()
    {
        if (!_autoCompile || !_autoPreview || !_ready || _busy || _disposed || _result?.Success != true || !IsCompilationCurrent(_result)) return;
        _busy = true;
        try
        {
            if (_isolationVisible) await ShowIsolatedCompilationAsync();
            else
            {
                _visualTree = await ShowTrustedCompilationAsync(_result);
                _previewShown = true;
                _automation.NotifyResourceChanged("xamlg://runtime");
            }
            _status = "Compilation succeeded · " + (_isolationVisible ? "isolated preview" : "preview") + " updated automatically";
        }
        catch (Exception error) { Report(error); }
        finally { _busy = false; }
    }

    public sealed class LiveUpdatePreferences
    {
        public bool Compile { get; set; } = true;
        public bool Preview { get; set; } = true;
    }
}
