using System.Text.Json;
using Microsoft.JSInterop;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;

namespace XamlG.Playground;

public partial class App
{
    private void AddComputerAutomation()
    {
        _automation.Add<ComputerViewportArguments, object>("xamlg_computer_viewport", "Resize the trusted or isolated preview viewport to 128–4096 DIPs for responsive testing. Omit both dimensions to follow the dock size. Observe a fresh frame after resizing.",
            AutomationScope.Runtime, AutomationEffect.Execute, async (args, context) =>
            {
                if ((args.Width == null) != (args.Height == null) || args.Width is < 128 or > 4096 || args.Height is < 128 or > 4096)
                    throw new ArgumentException("Supply both dimensions in 128–4096 DIPs, or omit both for automatic sizing.");
                _previewWidth = args.Width; _previewHeight = args.Height; StateHasChanged();
                await PrepareComputerViewportAsync(context.CancellationToken);
                await SaveShellStateAsync();
                return new { width = _previewWidth, height = _previewHeight, auto = _previewWidth == null };
            });
        _automation.Add<ComputerObserveOptions, JsonElement>("xamlg_computer_observe",
            "Observe the visible running preview: PNG screenshot, viewport/image coordinate mapping, revision, focus and bounded semantic control targets. Works in trusted and isolated preview. Re-observe after UI changes; use frameId and revision for actions.",
            AutomationScope.Runtime, AutomationEffect.Read, async (args, context) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await PrepareComputerViewportAsync(context.CancellationToken);
                return _isolationVisible ? await IsolatedComputerAsync("computer_observe", args) : ComputerResult(RuntimeInspector().ObserveComputer(args), designMode: _designMode);
            });
        _automation.Add<ComputerActionsRequest, JsonElement>("xamlg_computer_actions",
            "Use the preview like a computer. Execute up to 32 mouse, scroll, keyboard, text, touch, drag, focus, wait or assert actions then return a fresh screenshot. Coordinates use the observed image pixels by default; selectors can use objectId/name/automationId/text. Keys/text without a target use current focus. Scroll deltas are Avalonia wheel units. Actions stop on failure and report completed indices; never blindly replay a partial batch. Leaves visual design mode.",
            AutomationScope.Runtime, AutomationEffect.Execute, async (args, context) =>
            {
                await PrepareComputerViewportAsync(context.CancellationToken);
                if (_isolationVisible) return await IsolatedComputerAsync("computer_actions", args);
                Preview.IsDesignMode = _designMode = false;
                var result = await RuntimeInspector().ComputerActionsAsync(args, context.CancellationToken);
                return ComputerResult(result.Capture, result.Completed, result.Error, result.FailedIndex);
            });
    }
    private async Task PrepareComputerViewportAsync(CancellationToken cancellationToken)
    {
        // A tabbed preview has a zero-sized browser surface while another tool is
        // selected. Show it and allow the browser's resize observer to lay it out.
        await ShowPaneAsync("preview", focus: false);
        if (_module != null) await _module.InvokeVoidAsync("resizePreviewViewport", cancellationToken, _previewWidth, _previewHeight);
    }
    private async Task<JsonElement> IsolatedComputerAsync(string method, object args) => _isolatedPreview == null
        ? throw new InvalidOperationException("Run the isolated preview before interacting with it.")
        : await _isolatedPreview.ComputerAsync(method, args);

    internal static JsonElement ComputerResult(ComputerCapture capture, IReadOnlyList<ComputerActionResult>? completed = null,
        string? error = null, int? failedIndex = null, bool designMode = false)
    {
        var metadata = new { observation = capture.Observation, completed, error, failedIndex, designMode };
        return capture.Png == null ? AutomationJson.Element(metadata) : AutomationMedia.Image(metadata, capture.Png, capture.Observation.ImageWidth, capture.Observation.ImageHeight);
    }
    public sealed record ComputerViewportArguments(int? Width = null, int? Height = null);
}
