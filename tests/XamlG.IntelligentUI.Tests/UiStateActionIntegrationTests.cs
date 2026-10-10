using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiStateActionIntegrationTests
{
    [Fact]
    public async Task Tool_is_discoverable_and_uses_normal_authorization()
    {
        var store = new UiSessionStore();
        var snapshot = store.Publish(UiInteractionExamples.Counter(), "owner");
        var catalog = new AutomationCatalog(); using var ui = new UiAutomation(catalog, store);
        Assert.Equal(AutomationEffect.Edit, Assert.Single(catalog.Tools, t => t.Name == "xamlg_ui_state_action").Effect);
        var call = AutomationJson.Element(new UiActionCall(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/increment"));
        var result = await catalog.CallAsync("xamlg_ui_state_action", call, new("test", PrincipalId: "owner"));
        Assert.Equal(1, result.GetProperty("state").GetProperty("count").GetDecimal());
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_state_action", call, new("test", PrincipalId: "foreign")).AsTask());
        var denied = new AutomationCatalog((_, _) => ValueTask.FromResult(false));
        using var deniedUi = new UiAutomation(denied, store);
        await Assert.ThrowsAsync<AutomationException>(() => denied.CallAsync("xamlg_ui_state_action", call, new("test", PrincipalId: "owner")).AsTask());
    }

    [AvaloniaFact]
    public void Native_button_executes_locally_without_external_action_callback()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(UiInteractionExamples.Counter(), "owner");
        using var session = new UiAvaloniaSession(store, UiPresentation.From(snapshot), "owner");
        var external = 0; session.ActionRequested += _ => external++;
        var button = session.View.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Increment"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, session.Snapshot!.State.GetProperty("count").GetDecimal());
        Assert.Equal(0, external); Assert.Null(session.Diagnostic);
        // Reconciliation preserves the real control and its event subscription.
        Assert.Same(button, session.View.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Increment")));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, session.Snapshot!.State.GetProperty("count").GetDecimal());
    }
}
