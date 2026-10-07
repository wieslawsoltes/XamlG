using System.Text.Json;
using XamlG.Automation;
using XamlG.Runtime;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    // This callback belongs only to the local Razor component. It is neither a JS
    // invocation nor an automation transport method, and cannot be selected by MCP.
    private async Task<JsonElement> ExecuteRuntimeUiAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == name && tool.Scope == AutomationScope.Runtime)
            ?? throw new ArgumentException("Select a runtime operation.");
        await _automationGate.WaitAsync(cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_ready || _busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
            if (tool.Name == "xamlg_runtime_run") await CaptureEditorsAsync();
            _automationActivity.Add("call", name, "Studio owner", "started", 0);
            var result = await _automation.CallLocalAsync(name, arguments, new("Studio owner", cancellationToken, "studio-owner"));
            _automationActivity.Add("call", name, "Studio owner", "completed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception error)
        {
            _automationActivity.Add("call", name, "Studio owner", error is OperationCanceledException ? "cancelled" : "failed", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                error is AutomationException automation ? automation.Code : error.GetType().Name);
            throw;
        }
        finally { _automationGate.Release(); if (!_disposed) StateHasChanged(); }
    }

    private async Task NavigateRuntimeSourceAsync(XamlSourceInfo source)
    {
        try
        {
            await CaptureEditorsAsync();
            var syntax = source.Path == _document.Current.Path ? _document.Current : Compiler.Resources.Snapshot.GetValueOrDefault(source.Path);
            if (syntax == null || syntax.Version != source.Version) throw new InvalidOperationException("Source has changed since this preview. Run the preview again before navigating to its source.");
            var span = new TextSpan(source.Start, source.Length);
            if (span.Start < 0 || span.End > syntax.Text.Length) throw new InvalidOperationException("The source location is unavailable.");
            if (source.Path == _document.Current.Path)
            {
                _editorTab = "xaml"; await ShowPaneAsync("source");
                if (_xamlEditor != null) await _xamlEditor.RevealAsync(span);
            }
            else { _inspectorTab = "Resources"; _resourceEditor?.Reveal(source.Path, span); await ShowPaneAsync("inspector"); }
        }
        catch (Exception error) { Report(error); }
    }
}
