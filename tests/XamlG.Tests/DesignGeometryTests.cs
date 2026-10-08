using XamlG.Runtime;
using XamlG.Runtime.Design;
using Xunit;

namespace XamlG.Tests;

public sealed class DesignGeometryTests
{
    [Theory]
    [InlineData(XamlDesignArrangement.AlignLeft, 100, 20, 40, 30)]
    [InlineData(XamlDesignArrangement.AlignHorizontalCenter, 120, 20, 40, 30)]
    [InlineData(XamlDesignArrangement.AlignRight, 140, 20, 40, 30)]
    [InlineData(XamlDesignArrangement.AlignTop, 10, 80, 40, 30)]
    [InlineData(XamlDesignArrangement.AlignVerticalCenter, 10, 95, 40, 30)]
    [InlineData(XamlDesignArrangement.AlignBottom, 10, 110, 40, 30)]
    [InlineData(XamlDesignArrangement.SameWidth, 10, 20, 80, 30)]
    [InlineData(XamlDesignArrangement.SameHeight, 10, 20, 40, 60)]
    [InlineData(XamlDesignArrangement.SameSize, 10, 20, 80, 60)]
    public void Alignment_and_size_plans_follow_the_selected_anchor_without_mutating_inputs(XamlDesignArrangement operation, double x, double y, double width, double height)
    {
        XamlDesignRect[] before = [new(10, 20, 40, 30), new(100, 80, 80, 60)]; var original = before.ToArray();
        var result = XamlDesignGeometry.Arrange(before, operation, 1);
        Assert.Equal(new(x, y, width, height), result[0]); Assert.Equal(before[1], result[1]); Assert.Equal(original, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Distribution_preserves_endpoints_and_returns_equal_gaps_in_original_selection_order(bool vertical)
    {
        XamlDesignRect Rect(double start, double size) => vertical ? new(7, start, 8, size) : new(start, 7, size, 8);
        XamlDesignRect[] before = [Rect(100, 20), Rect(0, 10), Rect(20, 30)];
        var result = XamlDesignGeometry.Arrange(before, vertical ? XamlDesignArrangement.DistributeVertically : XamlDesignArrangement.DistributeHorizontally);
        Assert.Equal(before[0], result[0]); Assert.Equal(before[1], result[1]); Assert.Equal(Rect(40, 30), result[2]);
    }

    [Fact]
    public void Distribution_allows_equal_overlaps_when_the_fixed_endpoints_leave_no_free_space()
    {
        XamlDesignRect[] before = [new(0, 0, 100, 20), new(20, 10, 90, 30), new(40, 20, 100, 40)];
        var result = XamlDesignGeometry.Arrange(before, XamlDesignArrangement.DistributeHorizontally);
        Assert.Equal(before[0], result[0]); Assert.Equal(before[2], result[2]); Assert.Equal(25, result[1].X);
        Assert.Equal(result[1].X - result[0].Right, result[2].X - result[1].Right);
    }

    [Fact]
    public void Group_transforms_preserve_relative_positions_and_support_translating_zero_size_groups()
    {
        XamlDesignRect[] before = [new(10, 20, 20, 40), new(80, 80, 30, 40)];
        Assert.Equal(new(10, 20, 100, 100), XamlDesignGeometry.Union(before));
        Assert.Equal([new XamlDesignRect(30, 60, 40, 20), new(170, 90, 60, 20)],
            XamlDesignGeometry.Transform(before, XamlDesignGeometry.Union(before), new(30, 60, 200, 50)));
        Assert.Equal(new(30, 40, 0, 0), Assert.Single(XamlDesignGeometry.Transform([new(10, 20, 0, 0)], new(10, 20, 0, 0), new(30, 40, 0, 0))));
        Assert.Throws<ArgumentException>(() => XamlDesignGeometry.Transform([new(10, 20, 0, 0)], new(10, 20, 0, 0), new(30, 40, 1, 1)));
    }

    [Fact]
    public void Invalid_or_unbounded_geometry_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => XamlDesignGeometry.Union([]));
        Assert.Throws<ArgumentException>(() => XamlDesignGeometry.Union([new(double.MaxValue, 0, double.MaxValue, 1)]));
        Assert.Throws<ArgumentException>(() => XamlDesignGeometry.Arrange([new(0, 0, 10, 10)], XamlDesignArrangement.DistributeHorizontally));
        Assert.Throws<ArgumentOutOfRangeException>(() => XamlDesignGeometry.Arrange([new(0, 0, 10, 10)], XamlDesignArrangement.AlignLeft, 1));
    }

    [Fact]
    public void Aspect_locked_resize_can_collapse_to_zero_when_both_minimums_allow_it()
    {
        var gesture = new XamlDesignGesture(new XamlSourceInfo("View.xaml", 0, 7, "root", "hash"), new(0, 0, 100, 50),
            XamlResizeHandle.Right | XamlResizeHandle.Bottom, 0, 0);
        Assert.Equal(new(0, 0, 0, 0), gesture.Update(-100, -50, 0, true));
    }
}
