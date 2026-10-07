using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class EnumLiteralTests
{
    private const string Ns = ResourceProjectFixture.Namespace;

    [AvaloniaTheory]
    [InlineData("Stretch")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("4294967297")]
    [InlineData("-4294967294")]
    [InlineData("4294967296")]
    public void RegisteredPropertiesSettersAndSelectorsRetainEnumLiterals(string literal)
    {
        var xaml = "<StackPanel " + Ns + "><StackPanel.Styles>" +
            "<Style Selector='Button[HorizontalAlignment=" + literal + "]'><Setter Property='Tag' Value='matched'/></Style>" +
            "<Style Selector='Button.other'><Setter Property='HorizontalAlignment' Value='" + literal + "'/></Style>" +
            "</StackPanel.Styles><Button HorizontalAlignment='" + literal + "'/><Button Classes='other'/></StackPanel>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = new ResourceProjectFixture(new[] { ("Enums.axaml", xaml) }).Build("Enums.axaml");
        HorizontalAlignment? alignment = null;
        foreach (var root in new[] { expected.Root, actual })
        {
            var panel = Assert.IsType<StackPanel>(root);
            var window = new Window { Content = panel };
            try
            {
                window.Show(); window.UpdateLayout();
                alignment ??= panel.Children[0].HorizontalAlignment;
                Assert.All(panel.Children, child =>
                {
                    Assert.Equal(alignment, child.HorizontalAlignment);
                    Assert.Equal("matched", child.Tag);
                });
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("attribute", "left")]
    [InlineData("attribute", " Left ")]
    [InlineData("attribute", "Left,Right")]
    [InlineData("attribute", "9223372036854775808")]
    [InlineData("setter", "left")]
    [InlineData("setter", " Left ")]
    [InlineData("setter", "Left,Right")]
    [InlineData("setter", "9223372036854775808")]
    [InlineData("selector", "left")]
    [InlineData("selector", " Left ")]
    [InlineData("selector", "Left,Right")]
    [InlineData("selector", "9223372036854775808")]
    public void InvalidEnumLiteralsFailInEachFrameworkContext(string context, string literal)
    {
        var xaml = context switch
        {
            "attribute" => "<Button " + Ns + " HorizontalAlignment='" + literal + "'/>",
            "setter" => "<Style " + Ns + " Selector='Button'><Setter Property='HorizontalAlignment' Value='" + literal + "'/></Style>",
            _ => "<Style " + Ns + " Selector='Button[HorizontalAlignment=" + literal + "]'/>"
        };
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Enums.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("Left", HorizontalAlignment.Left)]
    [InlineData("4294967297", HorizontalAlignment.Left)]
    [InlineData("-1", (HorizontalAlignment)(-1))]
    [InlineData("9223372036854775807", (HorizontalAlignment)(-1))]
    public void BoxedEnumResourcesPreserveTheConvertedValue(string literal, HorizontalAlignment value)
    {
        var xaml = "<ResourceDictionary " + Ns + " xmlns:l='clr-namespace:Avalonia.Layout;assembly=Avalonia.Base'><l:HorizontalAlignment x:Key='value'>" + literal + "</l:HorizontalAlignment></ResourceDictionary>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = new ResourceProjectFixture(new[] { ("Enums.axaml", xaml) }).Build("Enums.axaml");
        foreach (var root in new[] { expected.Root, actual })
            Assert.Equal(value, Assert.IsType<HorizontalAlignment>(Assert.IsType<ResourceDictionary>(root)["value"]));
    }
}
