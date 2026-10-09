using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiContextualActionTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private static UiPublish Request(UiAction? action = null) => new("rows", 0, 1,
        """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
          <ui:Card ui:Key="row" ui:Each="{ui:Expr data.rows}" ui:ItemKey="{ui:Expr item.id}">
            <Button ui:Key="select" ui:Action="select" Content="{ui:Expr item.label}"/>
          </ui:Card>
        </StackPanel>
        """, J(new { selected = "", previous = "" }), Data(),
        [action ?? new("select", "state", Arguments: J(new { selected = "{ui:Expr item.id}", previous = "{ui:Expr state.selected}" }))]);
    private static JsonElement Data() => J(new { rows = new[]
    {
        new { id = "a", label = "Alpha", privateDetail = "context-only-sentinel" },
        new { id = "b", label = "Beta", privateDetail = "context-only-sentinel" }
    } });
    private static UiActionCall Call(UiSnapshot snapshot, string label)
    {
        var node = UiSessionStore.Flatten(snapshot.Roots).Single(n => n.ActionId == "select" && n.Properties["Content"].GetString() == label);
        return new(snapshot.Id, snapshot.Revision, snapshot.StateRevision, node.Key);
    }

    [Fact]
    public void Shared_action_resolves_the_clicked_row_and_atomic_pre_action_state()
    {
        var store = new UiSessionStore(); var initial = store.Publish(Request(), "owner");
        var first = store.ApplyStateAction(Call(initial, "Beta"), "owner");
        Assert.Equal("b", first.State.GetProperty("selected").GetString());
        Assert.Equal("", first.State.GetProperty("previous").GetString());
        var second = store.ApplyStateAction(Call(first, "Alpha"), "owner");
        Assert.Equal("a", second.State.GetProperty("selected").GetString());
        Assert.Equal("b", second.State.GetProperty("previous").GetString());
        Assert.Equal(initial.Revision, second.Revision);
        Assert.Equal(2, second.StateRevision);
    }

    [Fact]
    public void Context_is_not_serialized_into_render_operations()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(Request(), "owner");
        var json = JsonSerializer.Serialize(snapshot.Roots);
        Assert.DoesNotContain("context-only-sentinel", json);
        Assert.DoesNotContain("ActionItem", json);
        Assert.DoesNotContain("actionItem", json);
        Assert.Contains("Beta", json);
    }

    [Fact]
    public void Reordered_updated_data_uses_current_item_and_rejects_stale_clicks()
    {
        var store = new UiSessionStore();
        var initial = store.Publish(Request(new("select", "copy", Text: "{ui:Expr item.label}")), "owner");
        var old = Call(initial, "Alpha");
        var next = store.ChangeData(new(initial.Id, initial.Revision, J(new { rows = new[]
        { new { id = "b", label = "Beta v2" }, new { id = "a", label = "Alpha v2" } } })), "owner");
        Assert.Equal(old.NodeKey, Call(next, "Alpha v2").NodeKey);
        Assert.Equal("Alpha v2", store.PrepareAction(Call(next, "Alpha v2"), "owner").Text);
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.PrepareAction(old, "owner")).Code);
        var removed = store.ChangeData(new(next.Id, next.Revision, J(new { rows = Array.Empty<object>() })), "owner");
        Assert.Equal("invalid_action", Assert.Throws<UiException>(() => store.PrepareAction(old with { ExpectedRevision = removed.Revision }, "owner")).Code);
    }

    [Fact]
    public void Nested_repeat_captures_the_innermost_item()
    {
        var request = Request(new("select", "copy", Text: "{ui:Expr item.label}")) with
        {
            Xaml = """
                <StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
                  <StackPanel ui:Key="group" ui:Each="{ui:Expr data.groups}" ui:ItemKey="{ui:Expr item.id}">
                    <Button ui:Key="select" ui:Each="{ui:Expr item.rows}" ui:ItemKey="{ui:Expr item.id}" ui:Action="select" Content="{ui:Expr item.label}"/>
                  </StackPanel>
                </StackPanel>
                """,
            Data = J(new { groups = new[] { new { id = "parent", label = "Not the item", rows = new[] { new { id = "child", label = "Child" } } } } })
        };
        var store = new UiSessionStore(); var snapshot = store.Publish(request, "owner");
        Assert.Equal("Child", store.PrepareAction(Call(snapshot, "Child"), "owner").Text);
    }

    [Fact]
    public void Nested_tool_arguments_resolve_item_without_executing_the_tool()
    {
        var action = new UiAction("select", "tool", Tool: "inspect_record", Arguments: J(new
        { record = new { id = "{ui:Expr item.id}" }, labels = new[] { "{ui:Expr item.label}" } }));
        var store = new UiSessionStore(); var snapshot = store.Publish(Request(action), "owner");
        var intent = store.PrepareAction(Call(snapshot, "Beta"), "owner");
        Assert.Equal("b", intent.Arguments!.Value.GetProperty("record").GetProperty("id").GetString());
        Assert.Equal("Beta", intent.Arguments.Value.GetProperty("labels")[0].GetString());
        Assert.Same(snapshot, store.Read(snapshot.Id, "owner"));
        Assert.Equal("unknown_surface", Assert.Throws<UiException>(() => store.PrepareAction(Call(snapshot, "Beta"), "other")).Code);
    }

    [Fact]
    public void Archive_recompilation_recaptures_items_and_invalidates_old_calls()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(Request(), "owner");
        var old = Call(snapshot, "Beta");
        var archive = store.CaptureArchive("workspace");
        store.ReplaceArchive(archive, "workspace", store.Generation);
        var restored = store.Read(snapshot.Id, "owner");
        Assert.Equal("revision_conflict", Assert.Throws<UiException>(() => store.ApplyStateAction(old, "owner")).Code);
        var next = store.ApplyStateAction(Call(restored, "Beta"), "owner");
        Assert.Equal("b", next.State.GetProperty("selected").GetString());
    }

    [AvaloniaFact]
    public void Native_repeated_buttons_execute_with_their_own_context()
    {
        var store = new UiSessionStore(); var snapshot = store.Publish(Request(), "owner");
        using var view = new UiAvaloniaSession(store, UiPresentation.From(snapshot), "owner");
        var external = 0; view.ActionRequested += _ => external++;
        var button = view.View.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Beta"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("b", view.Snapshot!.State.GetProperty("selected").GetString());
        Assert.Equal(0, external); Assert.Null(view.Diagnostic);
    }
}
