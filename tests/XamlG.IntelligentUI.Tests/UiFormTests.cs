using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using XamlG.Automation;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFormTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiActionCall Call(UiSnapshot snapshot, string key = "/submit")
        => new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, key);
    private static UiSnapshot Change(UiSessionStore store, UiSnapshot snapshot, string key, object value)
        => store.ChangeState(new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, key, J(value)), "owner");
    private static UiElement Submit(UiSnapshot snapshot) => UiSessionStore.Flatten(snapshot.Roots).Single(n => n.Key == "/submit");

    [Fact]
    public void Invalid_fields_block_submission_but_remain_editable_and_explain_errors()
    {
        var store = new UiSessionStore(); var initial = store.Publish(UiFormExamples.Configuration(), "owner");
        Assert.False(Submit(initial).Properties["IsEnabled"].GetBoolean());
        Assert.Contains("Project: Enter a project name.", initial.FallbackMarkdown);
        Assert.Contains("Confirmation: Review the configuration first.", initial.FallbackMarkdown);
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.PrepareAction(Call(initial), "owner")).Code);
        var named = Change(store, initial, "project", "Demo");
        Assert.False(Submit(named).Properties["IsEnabled"].GetBoolean());
        var valid = Change(store, named, "approved", true);
        Assert.True(Submit(valid).Properties["IsEnabled"].GetBoolean());
        Assert.DoesNotContain("Enter a project name.", valid.FallbackMarkdown);
        var intent = store.PrepareAction(Call(valid), "owner");
        Assert.Equal("message", intent.Kind);
        Assert.Equal("Use project Demo with 1 seats.", intent.Text);
        Assert.Same(valid, store.Read(valid.Id, "owner"));
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.PrepareAction(Call(named), "owner")).Code);
    }
    [Fact]
    public void Cross_field_predicates_and_fresh_tool_data_revalidate_without_discarding_input()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(UiFormExamples.Configuration(), "owner");
        snapshot = Change(store, snapshot, "project", "Demo");
        snapshot = Change(store, snapshot, "approved", true);
        snapshot = Change(store, snapshot, "seats", 10);
        Assert.Equal(10, snapshot.State.GetProperty("seats").GetDecimal());
        Assert.False(Submit(snapshot).Properties["IsEnabled"].GetBoolean());
        Assert.Contains("The seat limit is exceeded.", snapshot.FallbackMarkdown);
        snapshot = store.ChangeData(new(snapshot.Id, snapshot.Revision, J(new { limit = 20 })), "owner");
        Assert.True(Submit(snapshot).Properties["IsEnabled"].GetBoolean());
        Assert.Equal(10, snapshot.State.GetProperty("seats").GetDecimal());
    }
    [Fact]
    public void Reset_actions_remain_available_when_submission_is_invalid()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(UiFormExamples.Configuration(), "owner");
        snapshot = Change(store, snapshot, "project", "Demo");
        Assert.False(Submit(snapshot).Properties["IsEnabled"].GetBoolean());
        var reset = store.ApplyStateAction(Call(snapshot, "/reset"), "owner");
        Assert.Equal("", reset.State.GetProperty("project").GetString());
        Assert.False(Submit(reset).Properties["IsEnabled"].GetBoolean());
    }
    [Fact]
    public async Task Mcp_cannot_prepare_or_execute_an_invalid_submit_button()
    {
        var store = new UiSessionStore(); var request = UiFormExamples.Configuration();
        request = request with { Actions = request.Actions!.Select(a => a.Id == "submit"
            ? new UiAction("submit", "state", Arguments: J(new { submitted = true })) : a).ToArray() };
        var snapshot = store.Publish(request, "owner");
        var catalog = new AutomationCatalog(); using var ui = new UiAutomation(catalog, store);
        foreach (var tool in new[] { "xamlg_ui_action", "xamlg_ui_state_action" })
            await Assert.ThrowsAsync<AutomationException>(() => catalog.CallAsync(tool, AutomationJson.Element(Call(snapshot)), new("test", PrincipalId: "owner")).AsTask());
        Assert.False(store.Read(snapshot.Id, "owner").State.GetProperty("submitted").GetBoolean());
        snapshot = Change(store, snapshot, "project", "Demo"); snapshot = Change(store, snapshot, "approved", true);
        var result = await catalog.CallAsync("xamlg_ui_state_action", AutomationJson.Element(Call(snapshot)), new("test", PrincipalId: "owner"));
        Assert.True(result.GetProperty("state").GetProperty("submitted").GetBoolean());
    }
    [AvaloniaFact]
    public void Native_form_retains_controls_and_emits_only_reviewable_external_intents()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(UiFormExamples.Configuration(), "owner");
        using var view = new UiAvaloniaSession(store, UiPresentation.From(snapshot), "owner");
        var input = view.View.GetLogicalDescendants().OfType<TextBox>().Single();
        var submit = view.View.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Use configuration"));
        Assert.False(submit.IsEnabled); Assert.Null(view.Diagnostic);
        snapshot = Change(store, snapshot, "project", "Demo"); snapshot = Change(store, snapshot, "approved", true);
        view.Refresh();
        Assert.Same(input, view.View.GetLogicalDescendants().OfType<TextBox>().Single());
        Assert.True(submit.IsEnabled); Assert.Equal("Demo", input.Text);
        var calls = new List<UiActionCall>(); view.ActionRequested += calls.Add;
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(calls); Assert.Null(view.Diagnostic);
        var after = store.Read(snapshot.Id, "owner");
        Assert.Equal(snapshot.StateRevision + 1, after.StateRevision);
        Assert.Equal(snapshot.Revision, after.Revision);
        Assert.True(JsonElement.DeepEquals(snapshot.State, after.State));
        Assert.True(Assert.Single(store.ReadForms(snapshot.Id, "owner")).Submitted);
        Assert.Equal(after.StateRevision, calls[0].ExpectedStateRevision);
        Assert.Equal("message", store.PrepareAction(calls[0], "owner").Kind);
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.PrepareAction(Call(snapshot), "owner")).Code);
    }
}
