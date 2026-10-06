using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class BindingPropertyElementTests
{
    [AvaloniaTheory]
    [InlineData("Binding")]
    [InlineData("data:ReflectionBinding")]
    public void SubstitutedBindingPreservesQualifiedPropertyElementsAndLocalNamespaces(string binding)
    {
        var source = "<StackPanel " + ResourceProjectFixture.Namespace +
            " xmlns:data='using:Avalonia.Data' xmlns:_xamlgBinding='urn:application-owned' x:CompileBindings='False'>" +
            "<TextBlock x:Name='source' Text='before'/><TextBlock><TextBlock.Text><MultiBinding StringFormat='{}{0}'>" +
            "<" + binding + " ElementName='source' Path='Text'><" + binding + ".Converter xmlns:c='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'>" +
            "<c:BindingUppercaseConverter/></" + binding + ".Converter></" + binding + "></MultiBinding></TextBlock.Text></TextBlock></StackPanel>";
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", source) });
        var panel = Assert.IsType<StackPanel>(fixture.Build("View.axaml"));
        var input = Assert.IsType<TextBlock>(panel.Children[0]);
        var output = Assert.IsType<TextBlock>(panel.Children[1]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("BEFORE", output.Text);
        input.Text = "after";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("AFTER", output.Text);
    }

    [Fact]
    public void ForeignPropertyOwnerIsNotReinterpretedAsABindingMember()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<TextBlock " + ResourceProjectFixture.Namespace + " x:CompileBindings='False'><TextBlock.Text><Binding>" +
                "<TextBlock.Converter><x:Null/></TextBlock.Converter></Binding></TextBlock.Text></TextBlock>")
        });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics), diagnostic => diagnostic.Code == "XG1005");
    }
}
