using System.Text.Json;
using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;

namespace XamlG.Playground;

public partial class App
{
    private void AddComputerAutomation()
    {
        _automation.Add<ComputerObserveOptions, JsonElement>("xamlg_computer_observe",
            "Observe the visible running preview: PNG screenshot, viewport/image coordinate mapping, revision, focus and bounded semantic control targets. Works in trusted and isolated preview. Re-observe after UI changes; use frameId and revision for actions.",
            AutomationScope.Runtime, AutomationEffect.Read, async (args, context) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                return _isolationVisible ? await IsolatedComputerAsync("computer_observe", args) : ComputerResult(RuntimeInspector().ObserveComputer(args), designMode: _designMode);
            });
        _automation.Add<ComputerActionsRequest, JsonElement>("xamlg_computer_actions",
            "Use the preview like a computer. Execute up to 32 mouse, scroll, keyboard, text, touch, drag, focus, wait or assert actions then return a fresh screenshot. Coordinates use the observed image pixels by default; selectors can use objectId/name/automationId/text. Keys/text without a target use current focus. Scroll deltas are Avalonia wheel units. Actions stop on failure and report completed indices; never blindly replay a partial batch. Leaves visual design mode.",
            AutomationScope.Runtime, AutomationEffect.Execute, async (args, context) =>
            {
                if (_isolationVisible) return await IsolatedComputerAsync("computer_actions", args);
                Preview.IsDesignMode = _designMode = false;
                var result = await RuntimeInspector().ComputerActionsAsync(args, context.CancellationToken);
                return ComputerResult(result.Capture, result.Completed, result.Error, result.FailedIndex);
            });
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
}
