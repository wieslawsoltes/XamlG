namespace XamlG.Runtime.Design;

public enum XamlDesignArrangement
{
    AlignLeft, AlignHorizontalCenter, AlignRight, AlignTop, AlignVerticalCenter, AlignBottom,
    SameWidth, SameHeight, SameSize, DistributeHorizontally, DistributeVertically
}

/// <summary>Framework-independent layout plans. Alignment and sizing use an explicit
/// anchor; distribution keeps the first and last item on that axis in place.</summary>
public static class XamlDesignGeometry
{
    public static IReadOnlyList<XamlDesignRect> Arrange(IReadOnlyList<XamlDesignRect> bounds,
        XamlDesignArrangement arrangement, int anchor = 0)
    {
        Validate(bounds);
        if (!Enum.IsDefined(typeof(XamlDesignArrangement), arrangement) || anchor < 0 || anchor >= bounds.Count)
            throw new ArgumentOutOfRangeException(nameof(arrangement));
        var reference = bounds[anchor]; var result = bounds.ToArray();
        if (arrangement is XamlDesignArrangement.DistributeHorizontally or XamlDesignArrangement.DistributeVertically)
        {
            if (bounds.Count < 3) throw new ArgumentException("Distribution requires at least three items.");
            var horizontal = arrangement == XamlDesignArrangement.DistributeHorizontally;
            var order = Enumerable.Range(0, bounds.Count).OrderBy(index => horizontal ? bounds[index].X : bounds[index].Y).ThenBy(index => index).ToArray();
            var first = bounds[order[0]]; var last = bounds[order[order.Length - 1]];
            var start = horizontal ? first.X : first.Y; var end = horizontal ? last.Right : last.Bottom;
            var gap = (end - start - bounds.Sum(item => horizontal ? item.Width : item.Height)) / (bounds.Count - 1);
            var position = start;
            for (var i = 0; i < order.Length; i++)
            {
                var index = order[i]; var item = bounds[index];
                // Preserve both endpoints exactly, including floating point rounding.
                result[index] = i == 0 || i == order.Length - 1 ? item : horizontal ? item with { X = position } : item with { Y = position };
                position += (horizontal ? item.Width : item.Height) + gap;
            }
        }
        else
            for (var i = 0; i < result.Length; i++)
            {
                var item = bounds[i];
                result[i] = arrangement switch
                {
                    XamlDesignArrangement.AlignLeft => item with { X = reference.X },
                    XamlDesignArrangement.AlignHorizontalCenter => item with { X = reference.X + (reference.Width - item.Width) / 2 },
                    XamlDesignArrangement.AlignRight => item with { X = reference.Right - item.Width },
                    XamlDesignArrangement.AlignTop => item with { Y = reference.Y },
                    XamlDesignArrangement.AlignVerticalCenter => item with { Y = reference.Y + (reference.Height - item.Height) / 2 },
                    XamlDesignArrangement.AlignBottom => item with { Y = reference.Bottom - item.Height },
                    XamlDesignArrangement.SameWidth => item with { Width = reference.Width },
                    XamlDesignArrangement.SameHeight => item with { Height = reference.Height },
                    XamlDesignArrangement.SameSize => item with { Width = reference.Width, Height = reference.Height },
                    _ => throw new ArgumentOutOfRangeException(nameof(arrangement))
                };
            }
        Validate(result); return result;
    }

    public static XamlDesignRect Union(IReadOnlyList<XamlDesignRect> bounds)
    {
        Validate(bounds); var x = bounds.Min(item => item.X); var y = bounds.Min(item => item.Y);
        var result = new XamlDesignRect(x, y, bounds.Max(item => item.Right) - x, bounds.Max(item => item.Bottom) - y);
        Validate(new[] { result }); return result;
    }

    public static IReadOnlyList<XamlDesignRect> Transform(IReadOnlyList<XamlDesignRect> bounds, XamlDesignRect before, XamlDesignRect after)
    {
        Validate(bounds); Validate(new[] { before, after });
        if (before.Width == 0 && after.Width != 0 || before.Height == 0 && after.Height != 0)
            throw new ArgumentException("A zero-size selection cannot be scaled on that axis.");
        var sx = before.Width == 0 ? 1 : after.Width / before.Width; var sy = before.Height == 0 ? 1 : after.Height / before.Height;
        var result = bounds.Select(item => new XamlDesignRect(after.X + (item.X - before.X) * sx,
            after.Y + (item.Y - before.Y) * sy, item.Width * sx, item.Height * sy)).ToArray();
        Validate(result); return result;
    }

    private static void Validate(IReadOnlyList<XamlDesignRect> bounds)
    {
        if (bounds == null || bounds.Count is < 1 or > 256 || bounds.Any(item => !item.IsValid || !XamlDesignRect.Finite(item.Right) || !XamlDesignRect.Finite(item.Bottom)))
            throw new ArgumentException("Supply one to 256 finite, non-negative-size rectangles.", nameof(bounds));
    }
}
