using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFormHostInteractionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Request(bool multiline = false) => new("form", 0, 1,
        $"""
        <ui:Form xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" ui:Key="form" ErrorMode="OnTouch">
          <ui:Field Label="Name" IsRequired="True"><TextBox ui:Key="name" ui:Bind="name" AcceptsReturn="{multiline}"/></ui:Field>
          <ui:SubmitButton ui:Key="send" ui:Action="send" Content="Send"/>
        </ui:Form>
        """, J(new { name = "Ada" }), Actions: [new("send", "message", Text: "Reviewed separately")]);

    [AvaloniaFact]
    public void Native_enter_submits_once_and_still_uses_external_review()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(), "owner");
        using var session = new UiAvaloniaSession(store, UiPresentation.From(initial), "owner");
        var calls = new List<UiActionCall>(); session.ActionRequested += calls.Add;
        var text = session.View.GetLogicalDescendants().OfType<TextBox>().Single();
        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        text.RaiseEvent(key);
        Assert.True(key.Handled); Assert.Single(calls);
        Assert.True(Assert.Single(store.ReadForms("form", "owner")).Submitted);
        Assert.Equal("message", store.PrepareAction(calls[0], "owner").Kind);
    }

    [AvaloniaFact]
    public void Multiline_enter_is_not_form_submission_and_blur_marks_touched()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(true), "owner");
        using var session = new UiAvaloniaSession(store, UiPresentation.From(initial), "owner");
        var calls = 0; session.ActionRequested += _ => calls++;
        var text = session.View.GetLogicalDescendants().OfType<TextBox>().Single();
        text.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(0, calls);
        text.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.True(Assert.Single(store.ReadForms("form", "owner")).IsTouched);
        Assert.Null(session.Diagnostic);
    }

    [Fact]
    public async Task Automation_discovers_form_tools_and_enforces_transport_identity()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(), "owner");
        var catalog = new AutomationCatalog(); using var ui = new UiAutomation(catalog, store);
        foreach (var name in new[] { "read", "touch", "submit", "reset", "validate" })
            Assert.Contains(catalog.Tools, tool => tool.Name == "xamlg_ui_form_" + name);
        var result = await catalog.CallAsync("xamlg_ui_form_submit", J(new UiFormCall("form", initial.Revision, 0, "/form")), new("test", PrincipalId: "owner"));
        Assert.Equal("/send", result.GetProperty("action").GetProperty("nodeKey").GetString());
        await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync("xamlg_ui_form_read", J(new UiRead("form")), new("test", PrincipalId: "foreign")).AsTask());
    }
}
