using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using XamlG.AvaloniaRuntime.Design;
using XamlG.Runtime;
using XamlG.Runtime.Design;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed record RuntimeDesignGeometry(string ObjectId, XamlDesignRect Bounds);
public sealed record RuntimeDesignTarget(string ObjectId, XamlSourceInfo Source, XamlDesignRect Bounds,
    double MinimumWidth, double MinimumHeight, double? MaximumWidth, double? MaximumHeight);
public sealed record RuntimeDesignPlan(long Revision, IReadOnlyList<RuntimeDesignGeometry> Geometry, IReadOnlyList<XamlVisualEdit> Edits);

public sealed partial class AvaloniaRuntimeInspector
{
    public IReadOnlyList<RuntimeDesignTarget> DesignTargets(IReadOnlyList<string> objectIds, long expectedRevision) =>
        DesignObjects(objectIds, expectedRevision).Select(entry => new RuntimeDesignTarget(entry.Node.Id, entry.Node.Source!, DesignBounds(entry.Control),
            entry.Control.MinWidth, entry.Control.MinHeight, double.IsFinite(entry.Control.MaxWidth) ? entry.Control.MaxWidth : null,
            double.IsFinite(entry.Control.MaxHeight) ? entry.Control.MaxHeight : null)).ToArray();

    /// <summary>Translates preview-root DIP geometry through the host's layout policy
    /// into source edits. Does not alter the running tree or execute generated code.</summary>
    public RuntimeDesignPlan PlanDesignGeometry(IReadOnlyList<RuntimeDesignGeometry> geometry, long expectedRevision,
        IAvaloniaDesignerLayoutPolicy? policy = null)
    {
        var targets = DesignObjects(geometry.Select(item => item.ObjectId).ToArray(), expectedRevision);
        policy ??= new AvaloniaDesignerLayoutPolicy();
        var edits = new List<XamlVisualEdit>();
        for (var i = 0; i < targets.Count; i++)
        {
            var (control, node) = targets[i]; var before = DesignBounds(control); var after = geometry[i].Bounds;
            if (!after.IsValid || !double.IsFinite(after.Right) || !double.IsFinite(after.Bottom) ||
                after.Width < control.MinWidth || after.Height < control.MinHeight || after.Width > control.MaxWidth || after.Height > control.MaxHeight)
                throw new ArgumentException("Geometry must be finite and respect the control's minimum/maximum dimensions.");
            if (before == after) continue;
            var resize = before.Width != after.Width || before.Height != after.Height;
            var properties = policy.GetPropertyEdits(control, before, after, resize ? XamlResizeHandle.Right | XamlResizeHandle.Bottom : XamlResizeHandle.Move);
            if (properties.Count != 0) edits.Add(new(node.Source!, properties));
        }
        // A custom policy can execute getters. Do not publish its plan against a
        // topology or revision that changed while it was being computed.
        DesignObjects(geometry.Select(item => item.ObjectId).ToArray(), expectedRevision);
        return new(Revision, geometry.ToArray(), edits);
    }

    public RuntimeDesignPlan PlanDesignArrangement(IReadOnlyList<string> objectIds, XamlDesignArrangement arrangement,
        long expectedRevision, string? anchorId = null, IAvaloniaDesignerLayoutPolicy? policy = null)
    {
        var targets = DesignObjects(objectIds, expectedRevision);
        var parent = targets[0].Control.GetVisualParent();
        if (targets.Any(entry => !ReferenceEquals(entry.Control.GetVisualParent(), parent)))
            throw new ArgumentException("Arrange controls that share the same visual parent.");
        var anchor = anchorId == null ? 0 : Array.IndexOf(objectIds.ToArray(), anchorId);
        if (anchor < 0) throw new ArgumentException("The arrangement anchor must be part of the selection.");
        var after = XamlDesignGeometry.Arrange(targets.Select(entry => DesignBounds(entry.Control)).ToArray(), arrangement, anchor);
        return PlanDesignGeometry(objectIds.Select((id, index) => new RuntimeDesignGeometry(id, after[index])).ToArray(), expectedRevision, policy);
    }

    public RuntimeNode? DesignHitTest(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Use finite preview-root DIP coordinates.");
        var snapshot = Capture();
        if (_root is not Visual root || root.GetVisualAt(new Point(x, y)) is not { } hit) return null;
        var source = AvaloniaVisualInspector.FindSource(root, hit);
        return source?.Instance is AvaloniaObject obj ? snapshot.Nodes.FirstOrDefault(node => node.Id == Id(obj) && node.IsSourceOwned) : null;
    }

    private IReadOnlyList<(Control Control, RuntimeNode Node)> DesignObjects(IReadOnlyList<string> objectIds, long expectedRevision)
    {
        if (objectIds.Count is < 1 or > 256 || objectIds.Distinct(StringComparer.Ordinal).Count() != objectIds.Count)
            throw new ArgumentException("Select one to 256 distinct live tree objects.");
        var snapshot = Capture();
        if (Revision != expectedRevision) throw new InvalidOperationException("Runtime revision changed. Inspect the designer again before planning geometry.");
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var targets = objectIds.Select(id =>
        {
            if (!nodes.TryGetValue(id, out var node) || !node.IsSourceOwned || node.Source == null || _objects[id] is not Control control)
                throw new ArgumentException("Select current controls with their own generated XAML source. Template internals require editing their owning source.");
            if (!control.IsEffectivelyVisible) throw new InvalidOperationException("Geometry requires a visible, realized control. Edit hidden control properties directly in source.");
            DesignBounds(control);
            return (Control: control, Node: node);
        }).ToArray();
        var selected = targets.Select(entry => entry.Control).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var target in targets)
        {
            if (target.Control.GetVisualAncestors().Any(ancestor => selected.Contains(ancestor))) throw new ArgumentException("A geometry selection cannot contain both an ancestor and its descendant.");
            var source = target.Node.Source!;
            if (snapshot.Nodes.Count(node => node.IsSourceOwned && node.Source is { } other && other.Path == source.Path && other.Version == source.Version && other.Start == source.Start && other.Length == source.Length) > 1)
                throw new InvalidOperationException("Several realized controls share this source element. Edit the shared XAML source directly.");
        }
        return targets;
    }
    private XamlDesignRect DesignBounds(Control control)
    {
        if (_root is not Visual root || control.TranslatePoint(default, root) is not { } point)
            throw new InvalidOperationException("The control is outside the preview's coordinate space.");
        return new(point.X, point.Y, control.Bounds.Width, control.Bounds.Height);
    }
}
