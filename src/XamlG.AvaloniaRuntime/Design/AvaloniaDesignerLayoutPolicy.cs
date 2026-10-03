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
        if (control.GetVisualParent() is Canvas)
        {
            if (dx != 0)
            {
                var left = Canvas.GetLeft(control);
                result["Canvas.Left"] = Number((double.IsNaN(left) ? control.Bounds.X : left) + dx);
                result["Canvas.Right"] = "NaN";
            }
            if (dy != 0)
            {
                var top = Canvas.GetTop(control);
                result["Canvas.Top"] = Number((double.IsNaN(top) ? control.Bounds.Y : top) + dy);
                result["Canvas.Bottom"] = "NaN";
            }
        }
        else if (dx != 0 || dy != 0)
        {
            var margin = control.Margin;
            result[nameof(control.Margin)] = string.Join(",", Number(margin.Left + dx), Number(margin.Top + dy), Number(margin.Right), Number(margin.Bottom));
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
