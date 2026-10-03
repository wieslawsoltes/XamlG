using XamlG.Runtime;
using XamlG.Runtime.Design;
using Xunit;

namespace XamlG.Tests;

public sealed class DesignGestureTests
{
    private static XamlSourceInfo Source => new("View.xaml", 0, 10, "node", "hash", version: 7);
    [Theory]
    [InlineData(XamlResizeHandle.Left, 20, 0, 20, 0, 80, 50)]
    [InlineData(XamlResizeHandle.Top, 0, 10, 0, 10, 100, 40)]
    [InlineData(XamlResizeHandle.Right, 20, 0, 0, 0, 120, 50)]
    [InlineData(XamlResizeHandle.Bottom, 0, 10, 0, 0, 100, 60)]
    [InlineData(XamlResizeHandle.Left | XamlResizeHandle.Top, 20, 10, 20, 10, 80, 40)]
    [InlineData(XamlResizeHandle.Right | XamlResizeHandle.Top, 20, 10, 0, 10, 120, 40)]
    [InlineData(XamlResizeHandle.Left | XamlResizeHandle.Bottom, 20, 10, 20, 0, 80, 60)]
    [InlineData(XamlResizeHandle.Right | XamlResizeHandle.Bottom, 20, 10, 0, 0, 120, 60)]
    public void EveryResizeHandleKeepsItsOppositeAnchor(XamlResizeHandle handle, double dx, double dy, double x, double y, double width, double height)
    {
        var gesture = new XamlDesignGesture(Source, new(0, 0, 100, 50), handle);
        Assert.Equal(new XamlDesignRect(x, y, width, height), gesture.Update(dx, dy));
        Assert.Equal(7, gesture.Source.Version);
    }
    [Fact]
    public void SnappingAndAspectLockAreDeterministic()
    {
        var move = new XamlDesignGesture(Source, new(0, 0, 100, 50), XamlResizeHandle.Move);
        Assert.Equal(new XamlDesignRect(16, -8, 100, 50), move.Update(13, -7, 8));
        var resize = new XamlDesignGesture(Source, new(0, 0, 100, 50), XamlResizeHandle.Right | XamlResizeHandle.Bottom);
        var result = resize.Update(20, 1, 1, true);
        Assert.Equal(2, result.Width / result.Height);
    }
    [Fact]
    public void CrossingOppositeEdgesClampsToMinimumDimensions()
    {
        var gesture = new XamlDesignGesture(Source, new(0, 0, 100, 50), XamlResizeHandle.Left | XamlResizeHandle.Top, 10, 5);
        Assert.Equal(new XamlDesignRect(90, 45, 10, 5), gesture.Update(1000, 1000));
    }
}
