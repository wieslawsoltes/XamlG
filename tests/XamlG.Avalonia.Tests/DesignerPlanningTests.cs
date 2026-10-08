using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Design;
using XamlG.AvaloniaRuntime.Inspection;
using XamlG.Runtime.Design;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Editing;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DesignerPlanningTests
{
    private const string Ns = ResourceProjectFixture.Namespace;

    [AvaloniaTheory]
    [InlineData("Left", "Top")]
    [InlineData("Center", "Top")]
    [InlineData("Right", "Top")]
    [InlineData("Stretch", "Top")]
    [InlineData("Left", "Center")]
    [InlineData("Left", "Bottom")]
    [InlineData("Left", "Stretch")]
    public void Geometry_edits_rebuild_at_the_requested_bounds_for_aligned_grid_children(string horizontal, string vertical)
    {
        var xaml = $"<Grid {Ns}><Border Name='item' Width='80' Height='50' Margin='4,6,8,10' HorizontalAlignment='{horizontal}' VerticalAlignment='{vertical}' Background='Red'/></Grid>";
        RoundTrip(xaml, new(20, 30), new(25, 15));
    }

    [AvaloniaTheory]
    [InlineData("Canvas.Left='40' Canvas.Top='30'")]
    [InlineData("Canvas.Right='40' Canvas.Bottom='30'")]
    [InlineData("Margin='4,6,8,10'")]
    public void Canvas_resize_keeps_the_requested_origin_with_opposite_anchors_and_margins(string positioning)
    {
        RoundTrip($"<Canvas {Ns}><Border Name='item' Width='80' Height='50' {positioning} Background='Red'/></Canvas>", default, new(25, 15));
    }

    [AvaloniaFact]
    public void Moving_a_canvas_child_without_explicit_anchors_keeps_its_margin_offset_once()
    {
        RoundTrip($"<Canvas {Ns}><Border Name='item' Width='80' Height='50' Margin='4,6,8,10' Background='Red'/></Canvas>", new(20, 30), default);
    }

    [AvaloniaTheory]
    [InlineData("Vertical")]
    [InlineData("Horizontal")]
    public void Group_geometry_in_a_stack_panel_rebuilds_without_accumulating_sibling_offsets(string orientation)
    {
        var xaml = $"<StackPanel {Ns} Orientation='{orientation}'><Border Name='a' Width='80' Height='50' Margin='4,6,8,10' Background='Red'/><Border Name='b' Width='60' Height='40' Margin='3,5,7,9' Background='Blue'/></StackPanel>";
        var root = Build(xaml); var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var state = inspector.Capture();
            var ids = new[] { state.Nodes.Single(node => node.Name == "a").Id, state.Nodes.Single(node => node.Name == "b").Id };
            var targets = inspector.DesignTargets(ids, inspector.Revision);
            var desired = targets.Select(target => new RuntimeDesignGeometry(target.ObjectId,
                target.Bounds with { X = target.Bounds.X + 12, Y = target.Bounds.Y + 16, Width = target.Bounds.Width + 20, Height = target.Bounds.Height + 10 })).ToArray();
            var plan = inspector.PlanDesignGeometry(desired, inspector.Revision);
            var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["View.axaml"] = xaml });
            var changes = XamlBatchDesignerEdits.FromVisualEdits(new Dictionary<string, XamlSyntaxTree> { ["View.axaml"] = XamlSyntaxTree.Parse(xaml, "View.axaml") }, plan.Edits);
            window.Content = Build(session.Apply(0, changes, "Group geometry", Validate).Documents["View.axaml"]); Pump(window);
            using var actual = new AvaloniaRuntimeInspector((Control)window.Content); var snapshot = actual.Capture();
            var actualIds = new[] { snapshot.Nodes.Single(node => node.Name == "a").Id, snapshot.Nodes.Single(node => node.Name == "b").Id };
            var result = actual.DesignTargets(actualIds, snapshot.Revision);
            Assert.Equal(desired.Select(item => item.Bounds), result.Select(item => item.Bounds));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Group_arrangement_is_one_compiler_validated_workspace_edit_and_undo()
    {
        var xaml = $"<Canvas {Ns}>\r\n<!--keep--><Border Name='a' Canvas.Left='10' Canvas.Top='20' Width='40' Height='30' Background='Red'/>\r\n<Border Name='b' Canvas.Left='100' Canvas.Top='80' Width='80' Height='60' Background='Blue'/>\r\n</Canvas>";
        var root = Build(xaml); var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var state = inspector.Capture();
            var a = state.Nodes.Single(node => node.Name == "a"); var b = state.Nodes.Single(node => node.Name == "b");
            var plan = inspector.PlanDesignArrangement([a.Id, b.Id], XamlDesignArrangement.AlignRight, state.Revision, b.Id);
            Assert.Equal(state.Revision, inspector.Revision); Assert.Equal(10, Assert.IsType<Canvas>(root).Children[0].Bounds.X);
            var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["View.axaml"] = xaml });
            var edits = XamlBatchDesignerEdits.FromVisualEdits(new Dictionary<string, XamlSyntaxTree> { ["View.axaml"] = XamlSyntaxTree.Parse(xaml, "View.axaml") }, plan.Edits);
            var result = session.Apply(0, edits, "Align group", Validate);
            Assert.Equal(1, result.Revision); Assert.Contains("<!--keep-->", result.Documents["View.axaml"]); Assert.Contains("\r\n", result.Documents["View.axaml"]);
            window.Content = Build(result.Documents["View.axaml"]); Pump(window);
            var canvas = Assert.IsType<Canvas>(window.Content);
            Assert.Equal(canvas.Children[1].Bounds.Right, canvas.Children[0].Bounds.Right);
            var undone = session.Undo(result.Revision); Assert.Equal(xaml, undone.Documents["View.axaml"]); Assert.False(session.CanUndo);
            Assert.Equal(result.Documents, session.Redo(undone.Revision).Documents);
            Assert.Throws<InvalidOperationException>(() => session.Apply(0, edits, "Stale group", Validate));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Multi_document_geometry_commits_and_undoes_atomically_with_real_compiler_validation()
    {
        var sources = new Dictionary<string, string>
        {
            ["View.axaml"] = $"<Canvas {Ns} Width='200' Height='100'>\r\n<!--first--><Border Name='a' Canvas.Left='10' Canvas.Top='20' Width='40' Height='30' Background='Red'/></Canvas>",
            ["Detail.axaml"] = $"<Canvas {Ns} Width='200' Height='100'>\n<!--second--><Border Name='b' Canvas.Left='30' Canvas.Top='40' Width='60' Height='20' Background='Blue'/></Canvas>"
        };
        var documents = sources.ToDictionary(pair => pair.Key, pair => XamlSyntaxTree.Parse(pair.Value, pair.Key));
        var root = new StackPanel();
        foreach (var source in sources) root.Children.Add(Assert.IsAssignableFrom<Control>(AvaloniaCompilation.Build(source.Value, true, source.Key)));
        var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var state = inspector.Capture();
            var ids = new[] { state.Nodes.Single(node => node.Name == "a").Id, state.Nodes.Single(node => node.Name == "b").Id };
            var targets = inspector.DesignTargets(ids, state.Revision);
            var plan = inspector.PlanDesignGeometry(targets.Select(target => new RuntimeDesignGeometry(target.ObjectId,
                target.Bounds with { X = target.Bounds.X + 12, Y = target.Bounds.Y + 16 })).ToArray(), state.Revision);
            Assert.Equal(2, plan.Edits.Count);
            var session = new XamlWorkspaceEditSession(sources); var before = session.Current;
            var bad = plan.Edits.ToArray(); bad[1] = bad[1] with { Properties = new Dictionary<string, string> { ["Width"] = "invalid-width" } };
            Assert.Throws<InvalidOperationException>(() => session.Apply(0, XamlBatchDesignerEdits.FromVisualEdits(documents, bad), "Invalid group", Validate));
            Assert.Same(before, session.Current); Assert.False(session.CanUndo);
            Assert.Throws<InvalidOperationException>(() => XamlBatchDesignerEdits.FromVisualEdits(documents, [plan.Edits[0], plan.Edits[0]]));
            var stale = new Dictionary<string, XamlSyntaxTree>(documents) { ["Detail.axaml"] = XamlSyntaxTree.Parse(sources["Detail.axaml"], "Detail.axaml", version: 1) };
            Assert.Throws<InvalidOperationException>(() => XamlBatchDesignerEdits.FromVisualEdits(stale, plan.Edits));
            var result = session.Apply(0, XamlBatchDesignerEdits.FromVisualEdits(documents, plan.Edits), "Move group", Validate);
            Assert.Equal(1, result.Revision);
            Assert.Contains("Canvas.Left='22'", result.Documents["View.axaml"]); Assert.Contains("Canvas.Left='42'", result.Documents["Detail.axaml"]);
            Assert.Contains("\r\n<!--first-->", result.Documents["View.axaml"]); Assert.Contains("\n<!--second-->", result.Documents["Detail.axaml"]);
            Assert.Equal(before.Documents, session.Undo(result.Revision).Documents); Assert.False(session.CanUndo);
            Assert.Equal(result.Documents, session.Redo(session.Current.Revision).Documents);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Geometry_targets_reject_stale_hidden_nested_duplicate_and_out_of_constraint_selections()
    {
        var root = Build($"<Canvas {Ns}><Border Name='item' Width='80' Height='50' MinWidth='20' MaxWidth='90' Background='Red'/></Canvas>");
        var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var state = inspector.Capture(); var node = state.Nodes.Single(item => item.Name == "item");
            var target = Assert.Single(inspector.DesignTargets([node.Id], state.Revision));
            Assert.Equal(20, target.MinimumWidth); Assert.Equal(90, target.MaximumWidth);
            Assert.Throws<ArgumentException>(() => inspector.DesignTargets([node.Id, node.Id], inspector.Revision));
            Assert.Throws<ArgumentException>(() => inspector.DesignTargets([state.RootId, node.Id], inspector.Revision));
            Assert.Throws<ArgumentException>(() => inspector.PlanDesignGeometry([new(node.Id, target.Bounds with { Width = 100 })], inspector.Revision));
            var control = Assert.IsType<Border>(inspector.Resolve(node.Id)); control.IsVisible = false;
            Assert.Throws<InvalidOperationException>(() => inspector.DesignTargets([node.Id], state.Revision));
            Assert.Throws<InvalidOperationException>(() => inspector.DesignTargets([node.Id], inspector.Revision));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_layout_policy_that_changes_runtime_state_cannot_publish_its_plan()
    {
        var root = Build($"<Canvas {Ns}><Border Name='item' Width='80' Height='50' Background='Red'/></Canvas>"); var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var node = inspector.Capture().Nodes.Single(item => item.Name == "item");
            var target = Assert.Single(inspector.DesignTargets([node.Id], inspector.Revision));
            Assert.Throws<InvalidOperationException>(() => inspector.PlanDesignGeometry([new(node.Id, target.Bounds with { X = 20 })], inspector.Revision, new MutatingPolicy()));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Hit_testing_template_visuals_returns_the_owning_source_control()
    {
        var xaml = $"<Canvas {Ns}><Button Name='item' Content='Button' Canvas.Left='20' Canvas.Top='30' Width='100' Height='50'/></Canvas>";
        var root = Build(xaml); var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var node = inspector.Capture().Nodes.Single(item => item.Name == "item");
            var hit = inspector.DesignHitTest(70, 55);
            Assert.NotNull(hit); Assert.Equal(node.Id, hit.Id); Assert.True(hit.IsSourceOwned);
            Assert.Equal("View.axaml", hit.Source!.Path); Assert.Equal(xaml.IndexOf("<Button", StringComparison.Ordinal), hit.Source.Start);
        }
        finally { window.Close(); }
    }

    private static void RoundTrip(string xaml, Point delta, Size sizeDelta)
    {
        var root = Build(xaml); var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var node = inspector.Capture().Nodes.Single(item => item.Name == "item");
            var target = Assert.Single(inspector.DesignTargets([node.Id], inspector.Revision));
            var after = new XamlDesignRect(target.Bounds.X + delta.X, target.Bounds.Y + delta.Y, target.Bounds.Width + sizeDelta.Width, target.Bounds.Height + sizeDelta.Height);
            var plan = inspector.PlanDesignGeometry([new(node.Id, after)], inspector.Revision);
            var session = new XamlWorkspaceEditSession(new Dictionary<string, string> { ["View.axaml"] = xaml });
            var changes = XamlBatchDesignerEdits.FromVisualEdits(new Dictionary<string, XamlSyntaxTree> { ["View.axaml"] = XamlSyntaxTree.Parse(xaml, "View.axaml") }, plan.Edits);
            var updated = session.Apply(0, changes, "Geometry", Validate);
            var next = Build(updated.Documents["View.axaml"]); window.Content = next; Pump(window);
            using var actual = new AvaloniaRuntimeInspector(next); var actualNode = actual.Capture().Nodes.Single(item => item.Name == "item");
            var bounds = Assert.Single(actual.DesignTargets([actualNode.Id], actual.Revision)).Bounds;
            Assert.Equal(after.X, bounds.X, precision: 6); Assert.Equal(after.Y, bounds.Y, precision: 6);
            Assert.Equal(after.Width, bounds.Width, precision: 6); Assert.Equal(after.Height, bounds.Height, precision: 6);
        }
        finally { window.Close(); }
    }
    private static Control Build(string xaml) => Assert.IsAssignableFrom<Control>(AvaloniaCompilation.Build(xaml, true, "View.axaml"));
    private static void Validate(XamlWorkspaceSnapshot candidate)
    {
        var compiled = new ResourceProjectFixture(candidate.Documents.Select(document => (document.Key, document.Value)), createSourceInfo: true);
        if (!compiled.Result.Success) throw new InvalidOperationException(string.Join(";", compiled.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
    }
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 }; window.Show(); Pump(window); return window;
    }
    private static void Pump(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private sealed class MutatingPolicy : IAvaloniaDesignerLayoutPolicy
    {
        public IReadOnlyDictionary<string, string> GetPropertyEdits(Control control, XamlDesignRect before, XamlDesignRect after, XamlResizeHandle handle)
        { control.Width++; return new Dictionary<string, string> { ["Canvas.Left"] = "20" }; }
    }
}
