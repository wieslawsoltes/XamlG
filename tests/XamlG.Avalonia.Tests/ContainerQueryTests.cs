using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ContainerQueryTests
{
    private const string Ns = ResourceProjectFixture.Namespace;

    [AvaloniaTheory]
    [InlineData("width:400")]
    [InlineData("min-width:400")]
    [InlineData("max-width:400")]
    [InlineData("height:300")]
    [InlineData("min-height:300")]
    [InlineData("max-height:300")]
    [InlineData("min-width:400 and max-height:300")]
    [InlineData("min-width:400, max-height:300")]
    [InlineData("min-width:400 and max-height:300, min-height:200")]
    [InlineData("min-width:400, max-height:300 and min-height:200")]
    [InlineData("min-width:400 and max-width:900 and max-height:300, min-height:200")]
    public void QueryConstructionMatchesThePinnedAvaloniaCompiler(string query)
    {
        var xaml = "<ContainerQuery " + Ns + " Query='" + query + "'><Style Selector='Button'><Setter Property='Width' Value='123'/></Style></ContainerQuery>";
        var expected = Assert.IsType<ContainerQuery>(AvaloniaRuntimeXamlLoader.Load(xaml));
        TestContext.Current.TestOutputHelper?.WriteLine("Upstream query: " + expected.Query?.ToString());
        var actual = Assert.IsType<ContainerQuery>(AvaloniaCompilation.Build(xaml));
        Assert.Equal(expected.Query?.ToString(), actual.Query?.ToString());
        Assert.Equal(123d, Assert.IsType<Setter>(Assert.IsType<Style>(Assert.Single(actual.Children)).Setters[0]).Value);
    }

    [AvaloniaFact]
    public void PropertyElementQueriesUseTheSameCompilerPath()
    {
        var xaml = "<ContainerQuery " + Ns + "><ContainerQuery.Query>max-width:400</ContainerQuery.Query></ContainerQuery>";
        var expected = Assert.IsType<ContainerQuery>(AvaloniaRuntimeXamlLoader.Load(xaml));
        var project = new ResourceProjectFixture(new[] { ("Query.axaml", xaml) });
        var actual = Assert.IsType<ContainerQuery>(project.Build("Query.axaml"));
        Assert.Equal(expected.Query!.ToString(), actual.Query!.ToString());
        var source = Assert.Single(project.Result.Documents).Output.Source;
        Assert.Contains("global::Avalonia.Styling.StyleQueries.@Width", source);
        Assert.DoesNotContain("AvaloniaRuntimeXamlLoader", source);
    }

    [AvaloniaFact]
    public void NamedContainerQueriesRespondToLayoutChangesLikeUpstream()
    {
        var xaml = "<Window " + Ns + " Width='300' Height='300'><Window.Styles>" +
            "<ContainerQuery Name='host' Query='max-width:400'><Style Selector='Border#child'><Setter Property='Tag' Value='narrow'/></Style></ContainerQuery>" +
            "<ContainerQuery Name='host' Query='min-width:401'><Style Selector='Border#child'><Setter Property='Tag' Value='wide'/></Style></ContainerQuery>" +
            "</Window.Styles><Border Container.Name='host' Container.Sizing='Width'><Border Name='child'/></Border></Window>";
        foreach (var build in new Func<string, object>[] { source => AvaloniaRuntimeXamlLoader.Load(source), AvaloniaCompilation.Build })
        {
            var window = Assert.IsType<Window>(build(xaml));
            try
            {
                window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var child = Assert.IsType<Border>(Assert.IsType<Border>(window.Content).Child);
                Assert.Equal("narrow", child.Tag);
                window.Width = 600;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal("wide", child.Tag);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("unknown:400")]
    [InlineData("width 400")]
    [InlineData("width:")]
    [InlineData("width:-1")]
    [InlineData("width:2.5")]
    [InlineData("width:1 or height:2")]
    [InlineData("(width:400)")]
    [InlineData("and width:400")]
    public void InvalidQueriesProduceLocatedCompilerDiagnostics(string query)
    {
        var xaml = "<ContainerQuery " + Ns + " Query='" + query + "'/>";
        Assert.NotNull(Record.Exception(() => AvaloniaRuntimeXamlLoader.Load(xaml)));
        var project = new ResourceProjectFixture(new[] { ("Query.axaml", xaml) });
        Assert.False(project.Result.Success);
        var diagnostic = Assert.Single(project.Result.Documents.Single().Output.Diagnostics.Where(d => d.Code == "XG3111"));
        Assert.Equal(XamlSeverity.Error, diagnostic.Severity);
        var start = xaml.IndexOf(query, StringComparison.Ordinal);
        Assert.InRange(diagnostic.Span.Start, start, start + query.Length);
    }
}
