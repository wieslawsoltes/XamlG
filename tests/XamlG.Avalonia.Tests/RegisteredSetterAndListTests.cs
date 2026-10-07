using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Input.GestureRecognizers;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RegisteredSetterAndListTests
{
    [AvaloniaFact]
    public void PrivateClrSetterUsesItsPublicRegistration()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<ScrollGestureRecognizer " +
            ResourceProjectFixture.Namespace + " Offset='12,34' Viewport='50,60' Extent='70,80'/>") });
        var gesture = Assert.IsType<ScrollGestureRecognizer>(fixture.Build("View.axaml"));
        Assert.Equal(new Vector(12, 34), gesture.Offset);
        Assert.Equal(new Size(50, 60), gesture.Viewport);
        Assert.Equal(new Size(70, 80), gesture.Extent);
    }

    [AvaloniaFact]
    public void RegisteredPrivateSetterAcceptsLiveElementBinding()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<ScrollViewer " + ResourceProjectFixture.Namespace +
            " x:Name='source' x:CompileBindings='False'><ScrollViewer.GestureRecognizers>" +
            "<ScrollGestureRecognizer Offset='{Binding Offset, ElementName=source}'/>" +
            "</ScrollViewer.GestureRecognizers></ScrollViewer>") });
        var source = Assert.IsType<ScrollViewer>(fixture.Build("View.axaml"));
        var gesture = Assert.IsType<ScrollGestureRecognizer>(Assert.Single(source.GestureRecognizers));
        Assert.Equal(source.Offset, gesture.Offset);
        source.Offset = new Vector(20, 40);
        Assert.Equal(source.Offset, gesture.Offset);
    }

    [AvaloniaTheory]
    [InlineData("1,2,3", 3)]
    public void NumericListLiteralConstructsTheDeclaredCollection(string literal, int count)
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Rectangle " + ResourceProjectFixture.Namespace +
            " StrokeDashArray='" + literal + "'/>") });
        var rectangle = Assert.IsType<Rectangle>(fixture.Build("View.axaml"));
        Assert.Equal(count, rectangle.StrokeDashArray!.Count);
        if (count != 0) Assert.Equal(new[] { 1d, 2d, 3d }, rectangle.StrokeDashArray);
    }

    [AvaloniaFact]
    public void EmptyNumericListLiteralPreservesTheDefaultValue()
    {
        var xaml = "<Rectangle " + ResourceProjectFixture.Namespace + " StrokeDashArray=''/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        Assert.Null(Assert.IsType<Rectangle>(baseline.Root).StrokeDashArray);
        Assert.Null(Assert.IsType<Rectangle>(AvaloniaCompilation.Build(xaml)).StrokeDashArray);
    }

    [AvaloniaFact]
    public void EnumPreferenceListRetainsOrder()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Window " + ResourceProjectFixture.Namespace +
            " TransparencyLevelHint='Transparent,Blur'/>") });
        var window = Assert.IsType<Window>(fixture.Build("View.axaml"));
        Assert.Equal(new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur }, window.TransparencyLevelHint);
    }

    [Fact]
    public void InvalidElementDoesNotProduceAPartialList()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Rectangle " + ResourceProjectFixture.Namespace +
            " StrokeDashArray='1,invalid,3'/>") });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics), diagnostic => diagnostic.Code == "XG1008");
    }
}
