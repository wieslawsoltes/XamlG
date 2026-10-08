using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    public RuntimeSnapshot CreateChild(string parentId, string typeName, IReadOnlyDictionary<string, RuntimeArgument>? initialValues, int index, long expectedRevision)
    {
        var parent = ResolveForMutation(parentId, expectedRevision);
        var type = ResolveType(typeName);
        if (!typeof(Control).IsAssignableFrom(type)) throw new ArgumentException("A tree child must derive from Control.");
        var slot = Container(parent); slot.ValidateInsert(index);
        var child = (Control)Construct(type, initialValues);
        ResolveForMutation(parentId, expectedRevision); // Constructors can execute application code.
        slot.Insert(child, index); return Capture();
    }

    public RuntimeSnapshot RemoveChild(string objectId, long expectedRevision)
    {
        var child = ResolveForMutation(objectId, expectedRevision) as Control ?? throw new ArgumentException("The object is not a control.");
        if (ReferenceEquals(child, _root)) throw new InvalidOperationException("The inspected root cannot be removed.");
        var parent = child.GetLogicalParent() ?? throw new InvalidOperationException("The control has no editable logical parent.");
        Container(parent).Remove(child); return Capture();
    }

    public RuntimeSnapshot Reparent(string objectId, string parentId, int index, long expectedRevision)
    {
        var child = ResolveForMutation(objectId, expectedRevision) as Control ?? throw new ArgumentException("The object is not a control.");
        var parent = ResolveForMutation(parentId, expectedRevision);
        if (ReferenceEquals(child, _root)) throw new InvalidOperationException("The inspected root cannot be reparented.");
        if (ReferenceEquals(child, parent) || child.GetLogicalDescendants().Any(item => ReferenceEquals(item, parent)) ||
            (parent is Avalonia.Visual visual && visual.GetVisualAncestors().Any(item => ReferenceEquals(item, child))))
            throw new ArgumentException("Reparenting would create a cycle.");
        var oldParent = child.GetLogicalParent() ?? throw new InvalidOperationException("The control has no editable logical parent.");
        var origin = Container(oldParent); var destination = Container(parent); var oldIndex = origin.IndexOf(child);
        if (oldIndex < 0) throw new InvalidOperationException("The framework owns this visual; edit its template or source instead.");
        if (ReferenceEquals(parent, oldParent))
        {
            if (index < 0) index = origin.Count - 1;
            if (index < 0 || index >= origin.Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (oldIndex == index) return Capture();
        }
        else destination.ValidateInsert(index);
        origin.Remove(child);
        try { destination.Insert(child, index); }
        catch
        {
            // Framework attachment can fail. Restore ownership when the child remains detached.
            if (child.GetLogicalParent() == null) origin.Insert(child, oldIndex);
            throw;
        }
        return Capture();
    }

    private static ChildContainer Container(object parent) => parent switch
    {
        Panel panel => new(() => panel.Children.Count, child => panel.Children.IndexOf(child),
            (child, index) => panel.Children.Insert(index < 0 ? panel.Children.Count : index, child), child => panel.Children.Remove(child)),
        Decorator decorator => new(() => decorator.Child == null ? 0 : 1, child => ReferenceEquals(decorator.Child, child) ? 0 : -1,
            (child, _) => decorator.Child = child, child => { if (!ReferenceEquals(decorator.Child, child)) throw new InvalidOperationException("This is not the parent's child."); decorator.Child = null; }, true),
        ContentControl content => new(() => content.Content == null ? 0 : 1, child => ReferenceEquals(content.Content, child) ? 0 : -1,
            (child, _) => content.Content = child, child => { if (!ReferenceEquals(content.Content, child)) throw new InvalidOperationException("This is not the parent's content."); content.Content = null; }, true),
        ItemsControl items when items.ItemsSource == null => new(() => items.Items.Count, child => items.Items.IndexOf(child),
            (child, index) => items.Items.Insert(index < 0 ? items.Items.Count : index, child), child => items.Items.Remove(child)),
        _ => throw new InvalidOperationException("This container is framework-owned or data-bound. Edit its source, template or items source instead.")
    };
    private sealed class ChildContainer(Func<int> count, Func<Control, int> indexOf, Action<Control, int> insert, Action<Control> remove, bool single = false)
    {
        public int Count => count();
        public int IndexOf(Control child) => indexOf(child);
        public void ValidateInsert(int index)
        { if (index < -1 || index > Count || (single && Count != 0)) throw new ArgumentException("The insertion index is invalid or the single-child container is occupied."); }
        public void Insert(Control child, int index) { ValidateInsert(index); insert(child, index); }
        public void Remove(Control child)
        { if (IndexOf(child) < 0) throw new InvalidOperationException("The framework owns this visual; edit its template or source instead."); remove(child); }
    }
}
