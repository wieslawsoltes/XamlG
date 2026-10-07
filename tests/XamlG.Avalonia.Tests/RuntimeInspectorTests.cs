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
        var textKey = TextBlock.TextProperty.OwnerType.FullName + "." + TextBlock.TextProperty.Name;
        text.Text = "user edit";
        Assert.Throws<InvalidOperationException>(() => inspector.SetProperty(before.RootId, textKey, Json("stale"), before.Revision));
        Assert.Equal("user edit", text.Text);
        var next = inspector.Capture();
        var property = inspector.SetProperty(next.RootId, textKey, Json("agent edit"), next.Revision);
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

    [AvaloniaFact]
    public void Runtime_creation_reordering_reparenting_and_removal_preserve_identity_and_source_independence()
    {
        var left = new StackPanel { Name = "left" }; var right = new Border { Name = "right" };
        var root = new StackPanel { Children = { left, right } };
        using var inspector = new AvaloniaRuntimeInspector(root);
        var state = inspector.Capture(); var leftId = state.Nodes.Single(node => node.Name == "left").Id; var rightId = state.Nodes.Single(node => node.Name == "right").Id;
        state = inspector.CreateChild(leftId, typeof(TextBlock).FullName!, new Dictionary<string, RuntimeArgument>
        { ["Name"] = new(Json("created")), ["Text"] = new(Json("Live")) }, -1, state.Revision);
        var created = state.Nodes.Single(node => node.Name == "created");
        Assert.Null(created.Source); Assert.Equal("Live", Assert.IsType<TextBlock>(left.Children[0]).Text);
        state = inspector.Reparent(created.Id, rightId, -1, state.Revision);
        Assert.Empty(left.Children); Assert.Same(right.Child, inspector.Resolve(created.Id));
        Assert.Equal(rightId, state.Nodes.Single(node => node.Id == created.Id).LogicalParent);
        Assert.Throws<ArgumentException>(() => inspector.Reparent(rightId, created.Id, -1, state.Revision));
        Assert.Throws<ArgumentException>(() => inspector.CreateChild(rightId, typeof(Button).FullName!, null, -1, state.Revision));
        state = inspector.RemoveChild(created.Id, state.Revision);
        Assert.Null(right.Child); Assert.DoesNotContain(state.Nodes, node => node.Id == created.Id);
        Assert.Throws<KeyNotFoundException>(() => inspector.Resolve(created.Id));
    }

    [AvaloniaFact]
    public void Tree_operations_reject_template_owned_children_without_detaching_them()
    {
        var button = new Button { Content = "content" }; var window = new Window { Content = button }; window.Show();
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(button);
            var state = inspector.Capture();
            var visual = state.Nodes.First(node => node.VisualParent == state.RootId);
            Assert.ThrowsAny<InvalidOperationException>(() => inspector.RemoveChild(visual.Id, state.Revision));
            Assert.Contains(inspector.Capture().Nodes, node => node.Id == visual.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Object_paths_inspect_and_mutate_view_models_collections_and_exact_methods()
    {
        var model = new InspectedModel(); var view = new TextBlock { DataContext = model };
        using var inspector = new AvaloniaRuntimeInspector(view); var state = inspector.Capture();
        var inspected = inspector.InspectObject(state.RootId, ["DataContext"]);
        Assert.Contains(inspected.Members, member => member.Name == "Title" && Equals(member.Value?.Value, "initial"));
        Assert.Contains("Rename(System.String)", inspected.Methods);
        inspector.SetObjectMember(state.RootId, ["DataContext", "Values", "0"], new(Json("edited")), inspector.Revision);
        Assert.Equal("edited", model.Values[0]);
        inspector.SetObjectMember(state.RootId, ["DataContext", "Labels", "name"], new(Json("dictionary")), inspector.Revision);
        Assert.Equal("dictionary", model.Labels["name"]);
        var result = await inspector.InvokeMethodAsync(state.RootId, ["DataContext"], "Rename(System.String)", [new(Json("renamed"))], inspector.Revision, TestContext.Current.CancellationToken);
        Assert.Equal("renamed", model.Title); Assert.Equal("renamed", result.Value);
        result = await inspector.InvokeMethodAsync(state.RootId, ["DataContext"], "RenameAsync(System.String)", [new(Json("awaited"))], inspector.Revision, TestContext.Current.CancellationToken);
        Assert.Equal("awaited", model.Title); Assert.Equal("awaited", result.Value);
        Assert.Throws<InvalidOperationException>(() => inspector.SetObjectMember(state.RootId, ["DataContext", "Title"], new(Json("stale")), state.Revision));
        Assert.Throws<ArgumentException>(() => inspector.SetObjectMember(state.RootId, ["DataContext", "Values", "10"], new(Json("missing")), inspector.Revision));
        inspector.CreateObjectMember(state.RootId, ["DataContext"], typeof(InspectedModel).FullName!, new Dictionary<string, RuntimeArgument> { ["Title"] = new(Json("new model")) }, inspector.Revision);
        Assert.NotSame(model, view.DataContext); Assert.Equal("new model", Assert.IsType<InspectedModel>(view.DataContext).Title);
    }

    [AvaloniaFact]
    public void Event_watches_retire_with_detached_controls_and_do_not_duplicate_handlers()
    {
        var button = new Button(); var root = new StackPanel { Children = { button } };
        using var inspector = new AvaloniaRuntimeInspector(root); var state = inspector.Capture();
        var id = state.Nodes.Single(node => node.Type == typeof(Button).FullName).Id;
        inspector.WatchEvent(id, "Click"); inspector.WatchEvent(id, "Click");
        var sequence = inspector.Changes().Sequence;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(inspector.Changes(sequence).Changes, change => change.Kind == "event");
        root.Children.Clear(); sequence = inspector.Changes().Sequence;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(inspector.Changes(sequence).Changes);
    }

    public sealed class InspectedModel
    {
        public string Title { get; set; } = "initial";
        public List<string> Values { get; } = ["one", "two"];
        public Dictionary<string, string> Labels { get; } = new() { ["name"] = "initial" };
        public string Rename(string title) => Title = title;
        public async ValueTask<string> RenameAsync(string title) { await Task.Yield(); return Title = title; }
    }

    [AvaloniaFact]
    public void Binding_inspection_updates_and_invalid_replacement_preserve_the_real_expression()
    {
        var model = new InspectedModel(); var text = new TextBlock { DataContext = model };
        using var inspector = new AvaloniaRuntimeInspector(text); var state = inspector.Capture();
        const string property = "Avalonia.Controls.TextBlock.Text";
        var binding = inspector.SetBinding(state.RootId, property, "Title", "TwoWay", null, state.Revision);
        Assert.Equal("initial", text.Text); Assert.Contains("Title", binding.Description);
        model.Title = "updated source";
        inspector.UpdateBinding(state.RootId, property, false, inspector.Revision);
        Assert.Equal("updated source", text.Text);
        Assert.ThrowsAny<Exception>(() => inspector.SetBinding(state.RootId, property, "[", "TwoWay", null, inspector.Revision));
        Assert.Single(inspector.Bindings(state.RootId)); Assert.Equal("updated source", text.Text);
        text.SetCurrentValue(TextBlock.TextProperty, "updated target");
        inspector.UpdateBinding(state.RootId, property, true, inspector.Revision);
        Assert.Equal("updated target", model.Title);
        inspector.ClearProperty(state.RootId, property, inspector.Revision);
        Assert.Empty(inspector.Bindings(state.RootId));
    }

    [AvaloniaFact]
    public void Styles_and_diagnostic_frames_explain_effective_values_and_restore_after_removal()
    {
        var text = new TextBlock(); text.Classes.Add("inspected");
        var panel = new StackPanel { Children = { text } }; var window = new Window { Content = panel }; window.Show();
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(panel); var state = inspector.Capture();
            var id = state.Nodes.Single(node => node.Type == typeof(TextBlock).FullName).Id; var original = text.FontSize;
            var fontSizeKey = TextBlock.FontSizeProperty.OwnerType.FullName + "." + TextBlock.FontSizeProperty.Name;
            var index = inspector.AddStyle(state.RootId, typeof(TextBlock).FullName!, "inspected", new Dictionary<string, RuntimeArgument>
            { [fontSizeKey] = new(Json(43d)) }, state.Revision);
            Assert.Equal(43, text.FontSize); Assert.Single(inspector.Styles(state.RootId));
            Assert.Contains(inspector.ValueFrames(id), frame => frame.Active && frame.Values.Any(value => value.Property.EndsWith(".FontSize") && Equals(value.Value.Value, 43d)));
            inspector.RemoveStyle(state.RootId, index, inspector.Revision);
            Assert.Equal(original, text.FontSize); Assert.Empty(inspector.Styles(state.RootId));
        }
        finally { window.Close(); }
    }
    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("Must not execute application formatting.");
    }
}
