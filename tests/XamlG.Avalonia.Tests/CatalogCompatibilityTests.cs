using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class CatalogCompatibilityTests
{
    [AvaloniaTheory]
    [InlineData("x")]
    [InlineData("language")]
    public void BindingMarkupAcceptsLanguageQualifiedDataType(string prefix)
    {
        var xaml = "<TextBlock " + ResourceProjectFixture.Namespace +
            " xmlns:language='http://schemas.microsoft.com/winfx/2006/xaml' Text='{CompiledBinding Length, " + prefix + ":DataType=x:String}'/>";
        foreach (var text in CompileBoth<TextBlock>(xaml))
        {
            text.DataContext = "initial";
            Assert.Equal("7", text.Text);
            text.DataContext = "new";
            Assert.Equal("3", text.Text);
        }
    }

    [AvaloniaFact]
    public void ClassesTypedMembersSupportSpaceSeparatedValues()
    {
        var xaml = "<Flyout " + ResourceProjectFixture.Namespace + " FlyoutPresenterClasses='one two one'/>";
        foreach (var flyout in CompileBoth<Flyout>(xaml))
            Assert.Equal(new[] { "one", "two" }, flyout.FlyoutPresenterClasses);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingSourceInfersStaticResourceTypeAndRetainsLiveUpdates(bool explicitDictionary)
    {
        const string resource = "<ScaleTransform x:Key='scale' ScaleX='2'/>";
        var resources = explicitDictionary ? "<ResourceDictionary>" + resource + "</ResourceDictionary>" : resource;
        var xaml = "<StackPanel " + ResourceProjectFixture.Namespace + "><StackPanel.Resources>" + resources +
            "</StackPanel.Resources><TextBlock Text='{CompiledBinding ScaleX, Source={StaticResource scale}}'/></StackPanel>";
        foreach (var panel in CompileBoth<StackPanel>(xaml))
        {
            var text = Assert.IsType<TextBlock>(Assert.Single(panel.Children));
            Assert.Equal("2", text.Text);
            ((ScaleTransform)panel.Resources["scale"]!).ScaleX = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("3", text.Text);
        }
    }

    [AvaloniaFact]
    public void NonPublicExternalResourcesRetainAvaloniaLoaderFailure()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", ResourceProjectFixture.Dictionary(ResourceProjectFixture.Include(
                "avares://avalonia.themes.simple/Controls/Button.xaml")))
        });
        // Avalonia deliberately does not export the internal Button dictionary through its URI loader.
        var error = Record.Exception(() => fixture.Build("View.axaml"));
        Assert.IsType<global::Avalonia.Markup.Xaml.XamlLoadException>(error!.GetBaseException());
        Assert.Contains("AvaloniaReferencedResource", fixture.Result.Documents[0].Output.Source);
        Assert.Contains("DynamicDependency", fixture.Result.Documents[0].Output.Source);
        Assert.Contains("CompiledAvaloniaXaml.!XamlLoader", fixture.Result.Documents[0].Output.Source);
    }

    [AvaloniaFact]
    public void StyleIncludeLoadsExistingPrecompiledAvaloniaLibrary()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<Styles " + ResourceProjectFixture.Namespace +
                "><StyleInclude Source='avares://Avalonia.Themes.Simple/SimpleTheme.xaml'/></Styles>")
        });
        var styles = Assert.IsType<Styles>(fixture.Build("View.axaml"));
        Assert.IsType<global::Avalonia.Themes.Simple.SimpleTheme>(Assert.Single(styles));
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("View.axaml", xaml) }).Build("View.axaml"));
    }
}
