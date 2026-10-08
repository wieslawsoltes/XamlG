using System.Globalization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using XamlG.Runtime.Design;

namespace XamlG.AvaloniaRuntime.Design;

/// <summary>Canvas coordinates for absolute layout; margins for other layouts. Resizes always produce explicit dimensions.</summary>
public sealed class AvaloniaDesignerLayoutPolicy : IAvaloniaDesignerLayoutPolicy
{
    public IReadOnlyDictionary<string, string> GetPropertyEdits(Control control, XamlDesignRect before, XamlDesignRect after, XamlResizeHandle handle)
    {
        if (!before.IsValid || !after.IsValid) throw new ArgumentOutOfRangeException(nameof(after));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dx = after.X - before.X; var dy = after.Y - before.Y;
        var dw = handle == XamlResizeHandle.Move ? 0 : after.Width - before.Width;
        var dh = handle == XamlResizeHandle.Move ? 0 : after.Height - before.Height;
        if (control.GetVisualParent() is Canvas)
        {
            var left = Canvas.GetLeft(control); var top = Canvas.GetTop(control);
            if (dx != 0 || dw != 0 && double.IsNaN(left) && !double.IsNaN(Canvas.GetRight(control)))
            {
                result["Canvas.Left"] = Number((double.IsNaN(left) ? control.Bounds.X - control.Margin.Left : left) + dx);
                result["Canvas.Right"] = "NaN";
            }
            if (dy != 0 || dh != 0 && double.IsNaN(top) && !double.IsNaN(Canvas.GetBottom(control)))
            {
                result["Canvas.Top"] = Number((double.IsNaN(top) ? control.Bounds.Y - control.Margin.Top : top) + dy);
                result["Canvas.Bottom"] = "NaN";
            }
        }
        else if (dx != 0 || dy != 0 || dw != 0 || dh != 0)
        {
            var margin = control.Margin;
            // Preserve the space reserved by the parent while shifting/resizing the
            // child inside it. This works for every alignment and keeps group edits
            // from moving later StackPanel siblings a second time.
            result[nameof(control.Margin)] = string.Join(",", Number(margin.Left + dx), Number(margin.Top + dy),
                Number(margin.Right - dx - dw), Number(margin.Bottom - dy - dh));
        }
        if (handle != XamlResizeHandle.Move)
        {
            result[nameof(control.Width)] = Number(after.Width);
            result[nameof(control.Height)] = Number(after.Height);
        }
        return result;
    }
    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
