using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using XamlG.AvaloniaRuntime.Inspection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeInspectorTests
{
    [AvaloniaFact]
    public void Visual_and_logical_trees_share_stable_object_identity()
    {
        var first = new Button { Name = "first", Content = "One" };
        var second = new Button { Name = "second", Content = "Two" };
        var panel = new StackPanel { Children = { first, second } };
        var window = new Window { Content = panel, Width = 400, Height = 300 };
        window.Show();
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(panel);
            var before = inspector.Capture();
            var firstNode = before.Nodes.Single(n => n.Name == "first");
            Assert.Equal(before.RootId, firstNode.VisualParent);
            Assert.Equal(before.RootId, firstNode.LogicalParent);
            Assert.NotEmpty(firstNode.VisualChildren);
            Assert.False(firstNode.VisualChildren.SequenceEqual(firstNode.LogicalChildren));
            panel.Children.Move(0, 1);
            var after = inspector.Capture();
            Assert.True(after.Revision > before.Revision);
            Assert.Equal(firstNode.Id, after.Nodes.Single(n => n.Name == "first").Id);
            Assert.Equal(firstNode.Id, after.Nodes.Single(n => n.Id == after.RootId).VisualChildren[1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Removed_handles_do_not_target_replacement_objects()
    {
        var old = new TextBlock { Name = "old", Text = "old" };
        var panel = new StackPanel { Children = { old } };
        using var inspector = new AvaloniaRuntimeInspector(panel);
        var before = inspector.Capture();
        var oldId = before.Nodes.Single(n => n.Name == "old").Id;
        var replacement = new TextBlock { Name = "new", Text = "untouched" };
        panel.Children[0] = replacement;
        Assert.Throws<KeyNotFoundException>(() => inspector.SetProperty(oldId, "Text", Json("changed"), before.Revision));
        Assert.Equal("untouched", replacement.Text);
        var after = inspector.Capture();
        Assert.NotEqual(oldId, after.Nodes.Single(n => n.Name == "new").Id);
    }

    [AvaloniaFact]
    public void External_property_changes_invalidate_mutation_revision()
    {
        var text = new TextBlock { Text = "one" };
        using var inspector = new AvaloniaRuntimeInspector(text);
        var before = inspector.Capture();
        text.Text = "user edit";
        Assert.Throws<InvalidOperationException>(() => inspector.SetProperty(before.RootId, "Text", Json("stale"), before.Revision));
        Assert.Equal("user edit", text.Text);
        var next = inspector.Capture();
        var property = inspector.SetProperty(next.RootId, "Text", Json("agent edit"), next.Revision);
        Assert.Equal("agent edit", text.Text);
        Assert.Equal("agent edit", property.Value!.Value);
        Assert.Equal("LocalValue", property.Priority);
        Assert.True(property.IsSet);
        Assert.Contains(inspector.Changes().Changes, e => e.Name.EndsWith(".Text") && Equals(e.Value?.Value, "user edit"));
    }

    [AvaloniaFact]
    public void Attached_properties_are_typed_and_clear_restores_default()
    {
        var text = new TextBlock();
        Grid.SetRow(text, 2);
        using var inspector = new AvaloniaRuntimeInspector(text);
        var state = inspector.Capture();
        var property = inspector.Properties(state.RootId).Single(p => p.Name == "Row");
        Assert.Equal("attached", property.Kind);
        inspector.SetProperty(state.RootId, property.Key, Json(3), state.Revision);
        Assert.Equal(3, Grid.GetRow(text));
        inspector.ClearProperty(state.RootId, property.Key, inspector.Revision);
        Assert.Equal(0, Grid.GetRow(text));
        Assert.False(inspector.Properties(state.RootId).Single(p => p.Name == "Row").IsSet);
        Assert.Throws<ArgumentException>(() => inspector.SetProperty(state.RootId, property.Key, Json("wrong"), inspector.Revision));
        Assert.Equal(0, Grid.GetRow(text));
    }

    [AvaloniaFact]
    public void Classes_resources_and_routed_events_change_real_controls()
    {
        var button = new Button();
        var clicked = 0;
        button.Click += (_, _) => clicked++;
        using var inspector = new AvaloniaRuntimeInspector(button);
        var state = inspector.Capture();
        inspector.SetClasses(state.RootId, ["primary", "accent", "primary"], state.Revision);
        Assert.Equal(["primary", "accent"], button.Classes.Where(c => !c.StartsWith(':')));
        inspector.SetResource(state.RootId, "Greeting", Json("Hello"), "string", false, inspector.Revision);
        Assert.Equal("Hello", inspector.FindResource(state.RootId, "Greeting").Value);
        inspector.WatchEvent(state.RootId, "Click");
        inspector.RaiseEvent(state.RootId, "Click", inspector.Revision);
        Assert.Equal(1, clicked);
        Assert.Contains(inspector.Changes().Changes, e => e.Kind == "event" && e.Name == "Click");
        inspector.ClearEventWatches();
        var sequence = inspector.Changes().Sequence;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(inspector.Changes(sequence).Changes);
    }

    [AvaloniaFact]
    public void Read_only_properties_invalid_literals_and_other_sessions_are_rejected()
    {
        var button = new Button();
        using var first = new AvaloniaRuntimeInspector(button);
        using var second = new AvaloniaRuntimeInspector(button);
        var state = first.Capture();
        Assert.Throws<KeyNotFoundException>(() => second.SetProperty(state.RootId, "Width", Json(100), second.Revision));
        var bounds = first.Properties(state.RootId).Single(p => p.Name == "Bounds");
        Assert.True(bounds.ReadOnly);
        Assert.Throws<InvalidOperationException>(() => first.SetProperty(state.RootId, bounds.Key, Json("1,2,3,4"), first.Revision));
        Assert.Throws<ArgumentException>(() => first.SetClasses(state.RootId, [":pointerover"], first.Revision));
    }

    [AvaloniaFact]
    public void Values_do_not_recursively_serialize_application_objects_or_nonfinite_numbers()
    {
        var button = new Button { DataContext = new ThrowingToString() };
        using var inspector = new AvaloniaRuntimeInspector(button);
        var state = inspector.Capture();
        var json = JsonSerializer.Serialize(new { state, properties = inspector.Properties(state.RootId) });
        Assert.Contains("NaN", json); // Unset Width is represented as text, valid JSON.
        Assert.Null(state.Nodes[0].DataContext!.Value);
    }

    [AvaloniaFact]
    public void Journal_is_bounded_and_reports_lost_history()
    {
        var text = new TextBlock();
        using var inspector = new AvaloniaRuntimeInspector(text);
        inspector.Capture();
        for (var i = 0; i < 1100; i++) text.Text = i.ToString();
        var changes = inspector.Changes();
        Assert.True(changes.HistoryLost);
        Assert.Equal(1024, changes.Changes.Count);
        inspector.Dispose();
        text.Text = "after disposal";
        Assert.Throws<ObjectDisposedException>(() => inspector.Capture());
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("Must not execute application formatting.");
    }
}
