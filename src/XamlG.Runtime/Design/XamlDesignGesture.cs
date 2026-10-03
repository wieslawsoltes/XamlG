namespace XamlG.Runtime.Design;

/// <summary>A deterministic gesture transaction in logical view coordinates; no framework or editor dependencies.</summary>
public sealed class XamlDesignGesture
{
    public XamlDesignGesture(XamlSourceInfo source, XamlDesignRect bounds, XamlResizeHandle handle,
        double minimumWidth = 1, double minimumHeight = 1)
    {
        if (!bounds.IsValid || minimumWidth < 0 || minimumHeight < 0 || !XamlDesignRect.Finite(minimumWidth) || !XamlDesignRect.Finite(minimumHeight))
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if ((handle & ~(XamlResizeHandle.Left | XamlResizeHandle.Top | XamlResizeHandle.Right | XamlResizeHandle.Bottom)) != 0 ||
            handle.HasFlag(XamlResizeHandle.Left) && handle.HasFlag(XamlResizeHandle.Right) || handle.HasFlag(XamlResizeHandle.Top) && handle.HasFlag(XamlResizeHandle.Bottom))
            throw new ArgumentOutOfRangeException(nameof(handle));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Initial = bounds; Handle = handle; MinimumWidth = minimumWidth; MinimumHeight = minimumHeight;
    }
    public XamlSourceInfo Source { get; }
    public XamlDesignRect Initial { get; }
    public XamlResizeHandle Handle { get; }
    public double MinimumWidth { get; }
    public double MinimumHeight { get; }

    public XamlDesignRect Update(double dx, double dy, double grid = 1, bool lockAspectRatio = false)
    {
        if (!XamlDesignRect.Finite(dx) || !XamlDesignRect.Finite(dy) || !XamlDesignRect.Finite(grid) || grid < 0) throw new ArgumentOutOfRangeException(nameof(dx));
        double Snap(double value) => grid > 0 ? Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid : value;
        if (Handle == XamlResizeHandle.Move) return Initial with { X = Snap(Initial.X + dx), Y = Snap(Initial.Y + dy) };
        var left = Initial.X; var top = Initial.Y; var right = Initial.Right; var bottom = Initial.Bottom;
        if (Handle.HasFlag(XamlResizeHandle.Left)) left = Math.Min(Snap(left + dx), right - MinimumWidth);
        if (Handle.HasFlag(XamlResizeHandle.Right)) right = Math.Max(Snap(right + dx), left + MinimumWidth);
        if (Handle.HasFlag(XamlResizeHandle.Top)) top = Math.Min(Snap(top + dy), bottom - MinimumHeight);
        if (Handle.HasFlag(XamlResizeHandle.Bottom)) bottom = Math.Max(Snap(bottom + dy), top + MinimumHeight);
        if (lockAspectRatio && Initial.Width > 0 && Initial.Height > 0)
        {
            var ratio = Initial.Width / Initial.Height;
            var horizontal = Handle.HasFlag(XamlResizeHandle.Left) || Handle.HasFlag(XamlResizeHandle.Right);
            var vertical = Handle.HasFlag(XamlResizeHandle.Top) || Handle.HasFlag(XamlResizeHandle.Bottom);
            var width = right - left; var height = bottom - top;
            if (horizontal && (!vertical || Math.Abs(width / Initial.Width - 1) >= Math.Abs(height / Initial.Height - 1))) height = width / ratio;
            else width = height * ratio;
            var scale = Math.Max(1, Math.Max(MinimumWidth / width, MinimumHeight / height));
            width *= scale; height *= scale;
            if (Handle.HasFlag(XamlResizeHandle.Left)) left = Initial.Right - width; else right = left + width;
            if (Handle.HasFlag(XamlResizeHandle.Top)) top = Initial.Bottom - height; else bottom = top + height;
        }
        return new(left, top, right - left, bottom - top);
    }
}
