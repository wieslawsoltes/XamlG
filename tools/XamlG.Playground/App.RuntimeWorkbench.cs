using System.Text.Json;
using XamlG.Automation;
using XamlG.Runtime;
using XamlG.Syntax;

namespace XamlG.Playground;

public partial class App
{
    private string? _runtimeSelectedId, _runtimeObjectHandle;
    private static string? RuntimePanel(string id) => id switch
    {
        "runtime" => "Properties", "runtime-objects" => "Objects", "runtime-bindings" => "Bindings",
        "runtime-styles" => "Styles", "runtime-resources" => "Resources", "runtime-events" => "Events",
        "runtime-input" => "Input", "runtime-accessibility" => "Accessibility", "runtime-tools" => "Tools", _ => null
    };
    private void RuntimeSelectionChanged(string id) => _runtimeSelectedId = id;
    private async Task OpenRuntimeObjectAsync(string id)
    { _runtimeObjectHandle = id; await ShowPaneAsync("runtime-objects"); }

    // This callback belongs only to the local Razor component. It is neither a JS
    // invocation nor an automation transport method, and cannot be selected by MCP.
    private async Task<JsonElement> ExecuteRuntimeUiAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var tool = _automation.Tools.SingleOrDefault(tool => tool.Name == name && tool.Scope is AutomationScope.Runtime or AutomationScope.Designer)
            ?? throw new ArgumentException("Select a runtime or designer operation.");
        await _automationGate.WaitAsync(cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!_ready || _busy || _disposed) throw new InvalidOperationException("Wait for the current IDE operation.");
            if (tool.Name == "xamlg_runtime_run" || tool.Scope == AutomationScope.Designer) await CaptureEditorsAsync();
            if (!_ready || _busy || _disposed) throw new InvalidOperationException("The IDE operation was superseded while capturing editors.");
            _automationActivity.Add("call", name, "Studio owner", "started", 0);
            var result = await _automation.CallLocalAsync(name, arguments, new("Studio owner", cancellationToken, "studio-owner"));
            if (tool.Scope == AutomationScope.Designer && tool.Effect == AutomationEffect.Edit) await SaveDraftAsync();
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
            await RevealDocumentAsync(source.Path, span);
        }
        catch (Exception error) { Report(error); }
    }
}
