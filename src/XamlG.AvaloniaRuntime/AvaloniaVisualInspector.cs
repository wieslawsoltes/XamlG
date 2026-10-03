using System.Collections.Immutable;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlG.Runtime;

namespace XamlG.AvaloniaRuntime;

/// <summary>Realized visual inspection with nearest generated-object source provenance.</summary>
public static class AvaloniaVisualInspector
{
    public static AvaloniaVisualNode Inspect(Visual root, int maximumNodes = 10000, int maximumDepth = 128)
    {
        ArgumentNullException.ThrowIfNull(root); Dispatcher.UIThread.VerifyAccess();
        if (maximumNodes < 1 || maximumDepth < 1) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var count = 0;
        AvaloniaVisualNode Visit(Visual visual, string id, int depth, XamlRuntimeSession? inheritedSession, XamlRuntimeNode? inheritedSource)
        {
            if (++count > maximumNodes || depth > maximumDepth) throw new InvalidOperationException("The visual tree exceeds the configured inspection limits.");
            var session = XamlRuntimeSession.TryGet(visual, out var ownedSession) ? ownedSession : inheritedSession;
            var exact = session?.FindNode(visual);
            var source = exact ?? inheritedSource;
            var bounds = visual.Bounds;
            var point = visual.TranslatePoint(default, root) ?? default;
            var children = visual.GetVisualChildren().Select((child, index) => Visit(child, id + "/" + index, depth + 1, session, source)).ToImmutableArray();
            return new(id, visual.GetType().FullName ?? visual.GetType().Name, (visual as StyledElement)?.Name,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, visual.IsVisible, children)
            { RuntimeKey = source?.Key, Source = source?.Source, IsSourceOwned = exact != null, RootX = point.X, RootY = point.Y };
        }
        return Visit(root, "visual", 0, null, null);
    }
    public static Visual? Find(Visual root, string id)
    {
        Dispatcher.UIThread.VerifyAccess();
        var parts = id.Split('/'); if (parts[0] != "visual") return null;
        Visual current = root;
        foreach (var part in parts.Skip(1))
        {
            if (!int.TryParse(part, out var index) || index < 0) return null;
            var next = current.GetVisualChildren().ElementAtOrDefault(index);
            if (next == null) return null; current = next;
        }
        return current;
    }
    public static XamlRuntimeNode? FindSource(Visual root, Visual visual)
    {
        Dispatcher.UIThread.VerifyAccess();
        for (Visual? candidate = visual; candidate != null; candidate = candidate.GetVisualParent())
        {
            for (Visual? owner = candidate; owner != null; owner = owner.GetVisualParent())
                if (XamlRuntimeSession.TryGet(owner, out var session) && session!.FindNode(candidate) is { Source: not null } node) return node;
            if (ReferenceEquals(candidate, root)) break;
        }
        return null;
    }
}
