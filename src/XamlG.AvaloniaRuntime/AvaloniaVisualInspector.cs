using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XamlG.AvaloniaRuntime;

/// <summary>Inspects realized framework visuals, including children created by control templates.</summary>
public static class AvaloniaVisualInspector
{
    public static AvaloniaVisualNode Inspect(Visual root, int maximumNodes = 10000, int maximumDepth = 128)
    {
        ArgumentNullException.ThrowIfNull(root);
        Dispatcher.UIThread.VerifyAccess();
        if (maximumNodes < 1 || maximumDepth < 1) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var count = 0;
        AvaloniaVisualNode Visit(Visual visual, string id, int depth)
        {
            if (++count > maximumNodes || depth > maximumDepth)
                throw new InvalidOperationException("The visual tree exceeds the configured inspection limits.");
            var bounds = visual.Bounds;
            var children = visual.GetVisualChildren().Select((child, index) => Visit(child, id + "/" + index, depth + 1)).ToImmutableArray();
            return new(id, visual.GetType().FullName ?? visual.GetType().Name, (visual as StyledElement)?.Name,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, visual.IsVisible, children);
        }
        return Visit(root, "visual", 0);
    }

    public static Visual? Find(Visual root, string id)
    {
        Dispatcher.UIThread.VerifyAccess();
        var parts = id.Split('/');
        if (parts[0] != "visual") return null;
        Visual current = root;
        foreach (var part in parts.Skip(1))
        {
            if (!int.TryParse(part, out var index) || index < 0) return null;
            var next = current.GetVisualChildren().ElementAtOrDefault(index);
            if (next == null) return null;
            current = next;
        }
        return current;
    }
}
