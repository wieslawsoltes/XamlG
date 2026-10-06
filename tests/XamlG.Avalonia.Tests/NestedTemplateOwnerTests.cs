using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace XamlG.Avalonia.Tests;

public sealed class NestedTemplateOwnerTests
{
    [AvaloniaTheory]
    [InlineData("{TemplateBinding Content}")]
    [InlineData("{TemplateBinding Property=Content}")]
    public void InnerControlOwnsItsTemplateInsteadOfTheOuterSlider(string binding)
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml",
            "<Slider " + ResourceProjectFixture.Namespace + " Width='200' Height='80'><Slider.Template><ControlTemplate>" +
            "<Button x:Name='inner' Content='initial'><Button.Template><ControlTemplate>" +
            "<ContentPresenter x:Name='part' Content='" + binding + "'/>" +
            "</ControlTemplate></Button.Template></Button></ControlTemplate></Slider.Template></Slider>") });
        var slider = Assert.IsType<Slider>(project.Build("View.axaml"));
        var window = new Window { Content = slider };
        try
        {
            window.Show(); window.UpdateLayout();
            var inner = slider.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "inner");
            var part = inner.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "part");
            Assert.Equal("initial", part.Content);
            inner.Content = "updated";
            Assert.Equal("updated", part.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TransitionUsesThePathPropertyNotTheOuterTemplatedControl()
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml", PathTemplate("Fill")) });
        var button = Assert.IsType<Button>(project.Build("View.axaml"));
        var window = new Window { Content = button };
        try
        {
            window.Show(); window.UpdateLayout();
            var path = button.GetVisualDescendants().OfType<ShapePath>().Single(control => control.Name == "part");
            var transition = Assert.IsType<BrushTransition>(Assert.Single(path.Transitions!));
            Assert.Same(ShapePath.FillProperty, transition.Property);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void UnknownPathPropertyMustNotFallBackToAnOuterButtonProperty()
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml", PathTemplate("Padding")) });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.SelectMany(document => document.Output.Diagnostics), diagnostic => diagnostic.Code == "XG1008");
    }

    private static string PathTemplate(string property) =>
        "<Button " + ResourceProjectFixture.Namespace + " Width='100' Height='60'><Button.Template><ControlTemplate>" +
        "<Path x:Name='part' Data='M0,0 L10,0 10,10 Z' Fill='Red'><Path.Transitions><Transitions>" +
        "<BrushTransition Property='" + property + "' Duration='0:0:0.1'/>" +
        "</Transitions></Path.Transitions></Path></ControlTemplate></Button.Template></Button>";
}
