using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFormInteractionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Request(string mode = "OnTouch", string validator = "", string value = "") => new("signup", 0, 1,
        $"""
        <ui:Form xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" ui:Key="form" ErrorMode="{mode}" Validator="{validator}">
          <ui:ValidationSummary ui:Key="summary"/>
          <ui:Field ui:Key="field" Label="Name" IsRequired="True"><TextBox ui:Key="name" ui:Bind="name"/></ui:Field>
          <ui:SubmitButton ui:Key="submit" ui:Action="send" Content="Submit"/>
        </ui:Form>
        """, J(new { name = value }), Actions: [new("send", "message", Text: "Submitted")]);
    private static UiFormCall Call(UiSnapshot snapshot, string? field = null) => new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/form", field);
    private static UiFormInteraction Form(UiSessionStore store) => Assert.Single(store.ReadForms("signup", "owner"));

    [Fact]
    public void Touch_and_dirty_are_distinct_and_errors_follow_the_selected_mode()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(), "owner");
        Assert.False(Form(store).IsTouched); Assert.False(Form(store).IsDirty);
        Assert.DoesNotContain("A value is required.", initial.FallbackMarkdown);
        var touched = store.TouchForm(Call(initial, "/name"), "owner");
        Assert.True(Form(store).IsTouched); Assert.False(Form(store).IsDirty);
        Assert.Contains("A value is required.", touched.FallbackMarkdown);
        var changed = store.ChangeState(new("signup", touched.Revision, touched.StateRevision, "name", J("Ada")), "owner");
        Assert.True(Form(store).IsDirty); Assert.True(Form(store).IsValid);
        Assert.Equal("", Form(store).Fields[0].InitialValue.GetString());
        var reset = store.ResetForm(Call(changed), "owner");
        Assert.Equal("", reset.State.GetProperty("name").GetString());
        Assert.False(Form(store).IsDirty); Assert.False(Form(store).IsTouched); Assert.False(Form(store).Submitted);
    }

    [Fact]
    public void Submission_reveals_errors_and_returns_the_first_invalid_input_without_an_action()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request("OnSubmit"), "owner");
        var touched = store.TouchForm(Call(initial, "/name"), "owner");
        Assert.DoesNotContain("A value is required.", touched.FallbackMarkdown);
        var result = store.SubmitForm(Call(touched), "owner");
        Assert.Null(result.Action); Assert.Equal("/name", result.FocusKey); Assert.False(result.RequiresValidation);
        Assert.Contains("A value is required.", result.Snapshot.FallbackMarkdown);
        var valid = store.ChangeState(new("signup", result.Snapshot.Revision, result.Snapshot.StateRevision, "name", J("Ada")), "owner");
        var submit = store.SubmitForm(Call(valid), "owner");
        Assert.NotNull(submit.Action); Assert.Null(submit.FocusKey);
        Assert.Equal("message", store.PrepareAction(submit.Action!, "owner").Kind);
    }

    [Fact]
    public async Task Async_validation_is_required_before_direct_actions_and_preserves_dirty_history()
    {
        var store = new UiSessionStore(); var calls = 0;
        store.RegisterFormValidator("remote", (context, _) => { calls++; Assert.Equal("owner", context.Principal); return ValueTask.FromResult<string?>(null); });
        var initial = store.Publish(Request(validator: "remote", value: "Ada"), "owner");
        Assert.Equal("invalid_form", Assert.Throws<UiException>(() => store.PrepareAction(new("signup", initial.Revision, 0, "/submit"), "owner")).Code);
        var submission = store.SubmitForm(Call(initial), "owner"); Assert.True(submission.RequiresValidation);
        var validated = await store.ValidateFormAsync(Call(submission.Snapshot), "owner");
        Assert.Equal(1, calls); Assert.True(Form(store).IsValid); Assert.False(Form(store).Pending);
        var action = store.SubmitForm(Call(validated), "owner").Action; Assert.NotNull(action);
        Assert.Equal("message", store.PrepareAction(action!, "owner").Kind);
        var changed = store.ChangeState(new("signup", validated.Revision, validated.StateRevision, "name", J("Grace")), "owner");
        Assert.False(Form(store).Validated); Assert.True(Form(store).IsDirty); Assert.False(Form(store).IsValid);
        Assert.Equal("invalid_form", Assert.Throws<UiException>(() => store.PrepareAction(new("signup", changed.Revision, changed.StateRevision, "/submit"), "owner")).Code);
    }

    [Fact]
    public async Task Delayed_validation_cannot_publish_over_new_input()
    {
        var store = new UiSessionStore(); var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RegisterFormValidator("remote", (_, _) => new(completion.Task));
        var initial = store.Publish(Request(validator: "remote", value: "Ada"), "owner");
        var running = store.ValidateFormAsync(Call(initial), "owner");
        var pending = store.Read("signup", "owner"); Assert.True(Form(store).Pending);
        var next = store.ChangeState(new("signup", pending.Revision, pending.StateRevision, "name", J("Grace")), "owner");
        completion.SetResult(null);
        Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<UiException>(() => running)).Code);
        Assert.Same(next, store.Read("signup", "owner")); Assert.False(Form(store).Pending); Assert.False(Form(store).Validated);
    }

    [Fact]
    public async Task Validator_errors_timeouts_and_missing_registrations_fail_closed()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(validator: "remote", value: "Ada"), "owner");
        Assert.Equal("unknown_validator", (await Assert.ThrowsAsync<UiException>(() => store.ValidateFormAsync(Call(initial), "owner"))).Code);
        store.RegisterFormValidator("remote", async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; });
        var result = await store.ValidateFormAsync(Call(initial), "owner", timeout: TimeSpan.FromMilliseconds(20));
        Assert.False(Form(store).Pending); Assert.False(Form(store).IsValid); Assert.NotNull(Form(store).AsyncError);
        Assert.Contains("timed out", result.FallbackMarkdown);
    }

    [Fact]
    public void Form_interactions_require_the_owner_and_both_revisions()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(), "owner");
        Assert.Equal("unknown_surface", Assert.Throws<UiException>(() => store.TouchForm(Call(initial, "/name"), "foreign")).Code);
        Assert.Equal("invalid_form", Assert.Throws<UiException>(() => store.TouchForm(Call(initial, "/missing"), "owner")).Code);
        store.TouchForm(Call(initial, "/name"), "owner");
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.SubmitForm(Call(initial), "owner")).Code);
    }
}
