using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Threading;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ElementBindingServiceTests
{
    [AvaloniaTheory]
    [InlineData("Tag", " RelativeSource='{RelativeSource Self}'")]
    [InlineData("$self.Tag", "")]
    public void CompiledMultiBindingSelfUsesReceivingControl(string path, string source)
    {
        var xaml = "<TextBlock " + ResourceProjectFixture.Namespace + " x:CompileBindings='True' Tag='before'>" +
            "<TextBlock.Text><MultiBinding StringFormat='{}{0}'><Binding Path='" + path + "'" + source +
            "/></MultiBinding></TextBlock.Text></TextBlock>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        foreach (var view in new[] { Assert.IsType<TextBlock>(baseline.Root),
                     Assert.IsType<TextBlock>(new ResourceProjectFixture(new[] { ("View.axaml", xaml) }).Build("View.axaml")) })
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("before", view.Text);
            view.Tag = "after";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("after", view.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("Binding", false)]
    [InlineData("Binding", true)]
    [InlineData("ReflectionBinding", false)]
    [InlineData("ReflectionBinding", true)]
    public void MultiBindingCapturesNamescopeForEachElementAndKeepsLiveUpdates(string element, bool forward)
    {
        const string inputs = "<TextBlock x:Name='first' Text='A'/><TextBlock x:Name='second' Text='B'/>";
        var output = "<TextBlock x:Name='output'><TextBlock.Text><MultiBinding StringFormat='{}{0}|{1}'><" + element +
            " ElementName='first' Path='Text'/><" + element + " ElementName='second' Path='Text'/></MultiBinding></TextBlock.Text></TextBlock>";
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<StackPanel " + ResourceProjectFixture.Namespace + " x:CompileBindings='False'>" +
                (forward ? output + inputs : inputs + output) + "</StackPanel>")
        });
        var panel = Assert.IsType<StackPanel>(fixture.Build("View.axaml"));
        var first = panel.Children.OfType<TextBlock>().Single(item => item.Name == "first");
        var second = panel.Children.OfType<TextBlock>().Single(item => item.Name == "second");
        var target = panel.Children.OfType<TextBlock>().Single(item => item.Name == "output");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("A|B", target.Text);
        first.Text = "changed";
        second.Text = "again";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("changed|again", target.Text);
    }

    [AvaloniaFact]
    public void RepeatedTemplateInstancesDoNotShareElementBindingScopes()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<ControlTemplate " + ResourceProjectFixture.Namespace + " TargetType='Button' x:CompileBindings='False'><StackPanel>" +
                "<TextBlock x:Name='source' Text='initial'/><TextBlock><TextBlock.Text><MultiBinding StringFormat='{}{0}'>" +
                "<Binding ElementName='source' Path='Text'/></MultiBinding></TextBlock.Text></TextBlock></StackPanel></ControlTemplate>")
        });
        var template = Assert.IsType<ControlTemplate>(fixture.Build("View.axaml"));
        var first = Assert.IsType<StackPanel>(template.Build(new Button())!.Result);
        var second = Assert.IsType<StackPanel>(template.Build(new Button())!.Result);
        var firstSource = Assert.IsType<TextBlock>(first.Children[0]);
        var firstTarget = Assert.IsType<TextBlock>(first.Children[1]);
        var secondSource = Assert.IsType<TextBlock>(second.Children[0]);
        var secondTarget = Assert.IsType<TextBlock>(second.Children[1]);
        firstSource.Text = "one";
        secondSource.Text = "two";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("one", firstTarget.Text);
        Assert.Equal("two", secondTarget.Text);
        firstSource.Text = "updated";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("updated", firstTarget.Text);
        Assert.Equal("two", secondTarget.Text);
    }

    [Fact]
    public void FailedCompiledChildDoesNotFallBackToReflection()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<TextBlock " + ResourceProjectFixture.Namespace + " x:CompileBindings='False'><TextBlock.Text><MultiBinding>" +
                "<CompiledBinding x:DataType='TextBlock' Path='DefinitelyMissingProperty'/></MultiBinding></TextBlock.Text></TextBlock>")
        });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics),
            diagnostic => diagnostic.Code.StartsWith("XG32", StringComparison.Ordinal));
    }
}
