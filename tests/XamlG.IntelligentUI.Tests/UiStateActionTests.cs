using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiStateActionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Request(object patch, string container = "StackPanel", string attributes = "") => new("counter", 0, 1,
        $"<{container} xmlns=\"https://github.com/avaloniaui\" xmlns:ui=\"urn:xamlg:intelligent-ui\" {attributes}><Button ui:Key=\"next\" ui:Action=\"advance\" Content=\"Next\"/><TextBlock Text=\"{{ui:Expr state.n.ToString()}}\"/></{container}>",
        J(new { n = 1, previous = 0 }), Actions: [new("advance", "state", Arguments: J(patch))]);
    private static UiActionCall Call(UiSnapshot snapshot) => new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/next");

    [Fact]
    public void Patch_expressions_share_pre_action_state_and_commit_once()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(new { n = "{ui:Expr state.n + 1}", previous = "{ui:Expr state.n}" }), "owner");
        var notifications = 0;
        store.Changed += _ => notifications++;
        var intent = store.PrepareAction(Call(initial), "owner");
        Assert.Equal("state", intent.Kind);
        Assert.Equal(initial, store.Read("counter", "owner"));
        var next = store.ApplyStateAction(Call(initial), "owner");
        Assert.Equal(2, next.State.GetProperty("n").GetDecimal());
        Assert.Equal(1, next.State.GetProperty("previous").GetDecimal());
        Assert.Equal(initial.Revision, next.Revision);
        Assert.Equal(initial.StateRevision + 1, next.StateRevision);
        Assert.Equal(1, notifications);
        Assert.Contains("2", next.FallbackMarkdown);
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "owner")).Code);
    }

    [Fact]
    public void Unknown_keys_roll_back_the_entire_patch()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(new { n = 2, missing = 3 }), "owner");
        var generation = store.Generation;
        Assert.Equal("unknown_state", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "owner")).Code);
        Assert.Same(initial, store.Read("counter", "owner"));
        Assert.Equal(generation, store.Generation);
    }

    [Fact]
    public void Invalid_types_do_not_mutate_state()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(new { n = "wrong" }), "owner");
        Assert.Equal("invalid_state", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "owner")).Code);
        Assert.Same(initial, store.Read("counter", "owner"));
    }

    [Fact]
    public void Evaluation_failure_leaves_last_valid_snapshot()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(new { n = "{ui:Expr 1 / state.previous}" }), "owner");
        Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "owner"));
        Assert.Same(initial, store.Read("counter", "owner"));
    }

    [Theory]
    [InlineData("StackPanel", "IsEnabled=\"False\"")]
    [InlineData("StackPanel", "IsVisible=\"False\"")]
    [InlineData("Expander", "IsExpanded=\"False\"")]
    public void Hidden_or_disabled_actions_are_rejected(string container, string attributes)
    {
        // Expander accepts a single child, so put the controls inside a StackPanel.
        var request = Request(new { n = 2 });
        if (container == "Expander") request = request with { Xaml = "<Expander xmlns=\"https://github.com/avaloniaui\" IsExpanded=\"False\">" + request.Xaml + "</Expander>" };
        else request = Request(new { n = 2 }, container, attributes);
        var store = new UiSessionStore(); var initial = store.Publish(request, "owner");
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "owner")).Code);
    }

    [Fact]
    public void Ownership_and_action_kind_are_not_bypassed()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(new { n = 2 }), "owner");
        Assert.Equal("unknown_surface", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(initial), "other")).Code);
        var external = store.Publish(Request(new { n = 2 }) with { Id = "external", Actions = [new("advance", "copy", Text: "text")] }, "owner");
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.ApplyStateAction(Call(external), "owner")).Code);
    }

    [Fact]
    public void Idempotent_patch_does_not_increment_revision_or_notify()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(new { n = 1 }), "owner");
        var notifications = 0; store.Changed += _ => notifications++;
        var generation = store.Generation;
        Assert.Same(initial, store.ApplyStateAction(Call(initial), "owner"));
        Assert.Equal(0, notifications); Assert.Equal(generation, store.Generation);
    }

    [Fact]
    public void Invalid_state_action_declarations_are_rejected()
    {
        foreach (var action in new[] { new UiAction("advance", "state"), new("advance", "state", Text: "not allowed", Arguments: J(new { n = 1 })), new("advance", "state", Tool: "tool", Arguments: J(new { n = 1 })), new("advance", "state", Arguments: J(new { })) })
        {
            var store = new UiSessionStore();
            Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.Publish(Request(new { n = 2 }) with { Actions = [action] }, "owner")).Code);
        }
    }
}
