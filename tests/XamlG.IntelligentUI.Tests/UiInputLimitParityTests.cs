using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiInputLimitParityTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Request() => new("bounded", 0, 1,
        """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
          <TextBox ui:Key="input" ui:Bind="text" MaxLength="4"/>
          <Button ui:Key="patch" ui:Action="patch" Content="Patch"/>
        </StackPanel>
        """, J(new { text = "ok", count = 0 }), Actions:
        [new("patch", "state", Arguments: J(new { count = 1, text = "too long" }))]);

    [Fact]
    public void Multi_key_action_cannot_bypass_declared_input_limits()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(Request(), "owner");
        var generation = store.Generation; var notifications = 0; store.Changed += _ => notifications++;
        Assert.Equal("invalid_property", Assert.Throws<UiException>(() => store.ApplyStateAction(
            new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, "/patch"), "owner")).Code);
        Assert.Same(snapshot, store.Read(snapshot.Id, "owner"));
        Assert.Equal(generation, store.Generation); Assert.Equal(0, notifications);
        Assert.Equal(0, snapshot.State.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Initial_source_and_streamed_constraints_are_validated_before_commit()
    {
        var store = new UiSessionStore();
        Assert.Equal("invalid_property", Assert.Throws<UiException>(() => store.Publish(
            Request() with { InitialState = J(new { text = "too long", count = 0 }) }, "owner")).Code);
        Assert.Empty(store.List("owner"));
        var original = store.Publish(Request(), "owner");
        Assert.Equal("invalid_property", Assert.Throws<UiException>(() => store.Publish(Request() with
        { ExpectedRevision = original.Revision, Sequence = 2, Xaml = Request().Xaml.Replace("MaxLength=\"4\"", "MaxLength=\"1\"", StringComparison.Ordinal) }, "owner")).Code);
        Assert.Same(original, store.Read(original.Id, "owner"));
    }

    [Fact]
    public void Tool_data_cannot_make_the_native_widget_disagree_with_resolved_state()
    {
        var store = new UiSessionStore();
        var request = Request() with
        {
            Xaml = Request().Xaml.Replace("ui:Bind=\"text\"", "Text=\"{ui:Expr data.text}\"", StringComparison.Ordinal),
            Data = J(new { text = "ok" })
        };
        var original = store.Publish(request, "owner");
        Assert.Equal("invalid_property", Assert.Throws<UiException>(() => store.ChangeData(
            new(original.Id, original.Revision, J(new { text = "too long" })), "owner")).Code);
        Assert.Same(original, store.Read(original.Id, "owner"));
    }

    [Fact]
    public void Transport_tree_validation_rejects_oversized_input_but_accepts_exact_limit()
    {
        var snapshot = new UiSessionStore().Publish(Request(), "owner");
        var input = UiSessionStore.Flatten(snapshot.Roots).Single(node => node.Type == "TextBox");
        var invalid = input with { Properties = input.Properties.SetItem("Text", J("12345")) };
        Assert.Equal("invalid_property", Assert.Throws<UiException>(() => UiTreeValidation.ValidateElement(
            invalid, UiCatalog.Default.Components["TextBox"])).Code);
        UiTreeValidation.ValidateElement(input with { Properties = input.Properties.SetItem("Text", J("1234")) }, UiCatalog.Default.Components["TextBox"]);
    }
}
