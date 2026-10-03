using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XamlG.AvaloniaRuntime.Design;

internal sealed class AvaloniaDesignOverlay : Control
{
    private readonly Pen _outline = new(new SolidColorBrush(Color.FromRgb(72, 105, 224)), 1);
    private readonly IBrush _fill = new SolidColorBrush(Color.FromArgb(22, 72, 105, 224));
    public Rect? Selection { get; set; }
    public Rect? Ghost { get; set; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Selection is not { } selection) return;
        context.DrawRectangle(null, _outline, selection);
        foreach (var point in Handles(selection)) context.DrawRectangle(Brushes.White, _outline, new Rect(point.X - 3, point.Y - 3, 6, 6));
        if (Ghost is { } ghost) context.DrawRectangle(_fill, _outline, ghost);
    }
    public static Point[] Handles(Rect rect) => new[]
    {
        rect.TopLeft, new Point(rect.Center.X, rect.Top), rect.TopRight,
        new Point(rect.Right, rect.Center.Y), rect.BottomRight,
        new Point(rect.Center.X, rect.Bottom), rect.BottomLeft, new Point(rect.Left, rect.Center.Y)
    };
}
