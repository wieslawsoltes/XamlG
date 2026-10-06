using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ResolveByNameRegressionTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlacementTargetResolvesBothOrdersAndBothNameSpellings(bool forward, bool ordinaryName)
    {
        var named = "<Border " + (ordinaryName ? "Name" : "x:Name") + "='anchor'/>";
        const string popup = "<Popup PlacementTarget='anchor'/>";
        var panel = Build(forward ? popup + named : named + popup);
        Assert.Same(panel.Children.OfType<Border>().Single(), panel.Children.OfType<Popup>().Single().PlacementTarget);
    }

    [AvaloniaFact]
    public void MissingNameCompletesAsNullRatherThanThrowingAnXReferenceError()
    {
        var panel = Build("<Popup PlacementTarget='missing'/>");
        Assert.Null(Assert.IsType<Popup>(Assert.Single(panel.Children)).PlacementTarget);
    }

    [AvaloniaFact]
    public void PropertyElementUsesTheSameForwardReferenceContract()
    {
        var panel = Build("<Popup><Popup.PlacementTarget>anchor</Popup.PlacementTarget></Popup><Border x:Name='anchor'/>");
        Assert.Same(panel.Children.OfType<Border>().Single(), panel.Children.OfType<Popup>().Single().PlacementTarget);
    }

    [AvaloniaFact]
    public void AttachedPropertyGetterOrSetterMetadataResolvesAnObjectTypedReference()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<RelativePanel " + ResourceProjectFixture.Namespace + "><Border RelativePanel.RightOf='anchor'/><Border x:Name='anchor'/></RelativePanel>")
        });
        var panel = Assert.IsType<RelativePanel>(fixture.Build("View.axaml"));
        Assert.Same(panel.Children[1], RelativePanel.GetRightOf(panel.Children[0]));
    }

    [Fact]
    public void ConflictingNameSpellingsAreRejected()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<Border " + ResourceProjectFixture.Namespace + " x:Name='first' Name='second'/>")
        });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(d => d.Output.Diagnostics), d => d.Code == "XG1012");
    }

    [Fact]
    public void UnannotatedControlPropertyDoesNotAcceptAnImplicitName()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<Decorator " + ResourceProjectFixture.Namespace + " Child='notAControlLiteral'/>")
        });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(d => d.Output.Diagnostics), d => d.Code == "XG1008");
    }

    private static StackPanel Build(string children)
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("View.axaml", "<StackPanel " + ResourceProjectFixture.Namespace + ">" + children + "</StackPanel>")
        });
        return Assert.IsType<StackPanel>(fixture.Build("View.axaml"));
    }
}
