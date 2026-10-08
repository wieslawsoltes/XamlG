using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;

namespace XamlG.Playground;

public partial class App
{
    private void AddRuntimeInputAutomation()
    {
        AddComputerAutomation();
        AddAutomation<RuntimeKeyArguments>("runtime_input_key", "Dispatch a typed keyboard press/down/up through the running Avalonia input pipeline. Focuses the target first; leave visual design mode before dispatching input.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => InputInspector().SendKey(args.ObjectId, args.Key, args.Action, args.ExpectedRevision, args.Modifiers, args.PhysicalKey, args.Symbol));
        AddAutomation<RuntimeTextArguments>("runtime_input_text", "Send typed text to a focused live control through Avalonia's text-input pipeline. Does not directly replace a Text property.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => InputInspector().SendText(args.ObjectId, args.Text, args.ExpectedRevision));
        AddAutomation<RuntimePointerArguments>("runtime_input_pointer", "Dispatch pointer movement, button down/up, click/double-click or wheel input. Coordinates are target-local DIPs; omitted coordinates use the target center. Down/move/up preserve capture for dragging.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => InputInspector().SendPointer(args.ObjectId, args.Action, args.ExpectedRevision, args.X, args.Y, args.Button, args.Modifiers, args.DeltaX, args.DeltaY));
        AddAutomation<RuntimeTouchArguments>("runtime_input_touch", "Begin, move, end or cancel a touch contact through Avalonia's input pipeline. Supports up to 16 live contacts with target-local DIP coordinates.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => InputInspector().SendTouch(args.ObjectId, args.ContactId, args.Action, args.X, args.Y, args.ExpectedRevision, args.Modifiers));
        AddAutomation<RevisionArguments>("runtime_input_reset", "Release automation-owned pointer capture and touch contacts for this runtime session.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.ResetInput(args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeAccessibilityArguments>("runtime_accessibility", "Page through the real Avalonia automation-peer tree, including virtual peers, names, roles, bounds, focus, relationships and supported provider interfaces.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().Accessibility(args.Offset, args.Count));
        AddAutomation<RuntimeAccessibilityProviderArguments>("runtime_accessibility_provider", "Inspect a live accessibility provider's public properties and exact method signatures, including invoke, toggle, value, range, selection and scrolling providers.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().AccessibilityProvider(args.PeerId, args.Provider));
        AddAutomation<RuntimeAccessibilityActionArguments>("runtime_accessibility_action", "Focus, bring into view or show the context menu for a current accessibility peer.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var result = runtime.AccessibilityAction(args.PeerId, args.Action, args.ExpectedRevision); return new { runtime.Revision, result }; });
        _automation.Add<RuntimeAccessibilityInvokeArguments, object>("xamlg_runtime_accessibility_invoke", "Invoke an exact public accessibility-provider method with typed literal or live-object arguments. Rejects stale peer handles and runtime revisions.", AutomationScope.Runtime, AutomationEffect.Execute,
            async (args, context) =>
            {
                var runtime = RuntimeInspector();
                var result = await runtime.InvokeAccessibilityProviderAsync(args.PeerId, args.Provider, args.Signature, args.Arguments, args.ExpectedRevision, context.CancellationToken);
                return new { runtime.Revision, result };
            });
    }
    private AvaloniaRuntimeInspector InputInspector()
    {
        if (_designMode) throw new InvalidOperationException("Leave visual design mode before sending runtime input.");
        return RuntimeInspector();
    }
    public sealed record RuntimeKeyArguments(string ObjectId, string Key, long ExpectedRevision, RuntimeKeyAction Action = RuntimeKeyAction.Press, string[]? Modifiers = null, string? PhysicalKey = null, string? Symbol = null);
    public sealed record RuntimeTextArguments(string ObjectId, string Text, long ExpectedRevision);
    public sealed record RuntimePointerArguments(string ObjectId, RuntimePointerAction Action, long ExpectedRevision, double? X = null, double? Y = null, string Button = "Left", string[]? Modifiers = null, double DeltaX = 0, double DeltaY = 0);
    public sealed record RuntimeTouchArguments(string ObjectId, long ContactId, RuntimeTouchAction Action, double X, double Y, long ExpectedRevision, string[]? Modifiers = null);
    public sealed record RuntimeAccessibilityArguments(int Offset = 0, int Count = 100);
    public sealed record RuntimeAccessibilityProviderArguments(string PeerId, string Provider);
    public sealed record RuntimeAccessibilityActionArguments(string PeerId, RuntimeAccessibilityAction Action, long ExpectedRevision);
    public sealed record RuntimeAccessibilityInvokeArguments(string PeerId, string Provider, string Signature, RuntimeArgument[] Arguments, long ExpectedRevision);
}
