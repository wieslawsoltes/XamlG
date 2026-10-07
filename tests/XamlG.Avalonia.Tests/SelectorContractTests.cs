using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SelectorContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("<Style.Selector/>")]
    [InlineData("<Style.Selector><x:String>Button</x:String></Style.Selector>")]
    [InlineData("<Style.Selector><x:Null/></Style.Selector>")]
    [InlineData("<Style.Selector><x:String>Button</x:String><x:String>Border</x:String></Style.Selector>")]
    [InlineData("<Style.Selector>Button<x:String>Border</x:String></Style.Selector>")]
    [InlineData("<Style.Selector>Button<!--comment-->.accent</Style.Selector>")]
    [InlineData("<Style.Selector>{x:Null}</Style.Selector>")]
    public void SelectorsRequireExactlyOneTextValue(string content)
    {
        var xaml = "<Style " + Ns + ">" + content + "</Style>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var actual = new ResourceProjectFixture(new[] { ("Style.axaml", xaml) });
        Assert.False(actual.Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("{x:Null}")]
    [InlineData("{x:Static t:SelectorContractValues.Button}")]
    [InlineData("Button,,Border")]
    [InlineData("Button[Missing=True]")]
    [InlineData(".accent[(Grid.Row)=1]")]
    [InlineData("Button[(Button.IsDefault)=True]")]
    [InlineData("t|SelectorPropertyControl[Unwrapped=7]")]
    [InlineData("t|PathHiddenDataContextControl[DataContext=custom]")]
    public void InvalidSelectorAttributesAreRejected(string selector)
    {
        var xaml = "<Style " + Ns + " Selector='" + selector + "'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("<Style.Selector>Button</Style.Selector>", "Button")]
    [InlineData("<Style.Selector><![CDATA[Button]]></Style.Selector>", "Button")]
    public void TextSelectorsRetainTheirValue(string content, string selector)
    {
        var xaml = "<Style " + Ns + ">" + content + "<Setter Property='Width' Value='42'/></Style>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(Assert.IsType<Style>(expected.Root).Selector!.ToString(), actual.Selector!.ToString());
        Assert.Equal(selector, actual.Selector.ToString());
        Assert.Equal(42d, Assert.IsType<Setter>(Assert.Single(actual.Setters)).Value);
    }

    [AvaloniaTheory]
    [InlineData("<Style.Selector xmlns:c='https://github.com/avaloniaui'>c|Button</Style.Selector>", "Button")]
    [InlineData("<Style.Selector xmlns:c='https://github.com/avaloniaui'>c|Button[(c|Grid.Row)=1]</Style.Selector>", "Button[(Grid.Row)=1]")]
    [InlineData("<Style.Selector>  Button  </Style.Selector>", "Button")]
    public void NativeSelectorTextPreservesPropertyNamespacesAndWhitespaceTolerance(string content, string selector)
    {
        var xaml = "<Style " + Ns + ">" + content + "</Style>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var actual = Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(selector, actual.Selector!.ToString());
    }

    [AvaloniaTheory]
    [InlineData("Button >")]
    [InlineData("Button > > TextBlock")]
    [InlineData("Button > /template/ TextBlock")]
    [InlineData("{}Button")]
    public void SelectorsRetainSupportedEscapesAndTrailingCombinators(string text)
    {
        var xaml = "<Style " + Ns + " Selector='" + text + "'/>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(Assert.IsType<Style>(expected.Root).Selector!.ToString(), actual.Selector!.ToString());
    }

    [AvaloniaTheory]
    [InlineData("t:PathRegistrationDerived", "Wrapped=base")]
    [InlineData("t:SelectorPropertyControl", "Token=enabled")]
    [InlineData("t:SelectorPropertyControl", "Different=7")]
    public void PropertySelectorsUseTheClrValueTypeAndDeclaringRegistration(string type, string property)
    {
        var xaml = "<StackPanel " + Ns + "><StackPanel.Styles><Style Selector='" + type.Replace(':', '|') + "[" + property + "]'><Setter Property='Width' Value='42'/></Style></StackPanel.Styles><" + type + "/></StackPanel>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        foreach (var root in new[] { expected.Root, new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml") })
        {
            var panel = Assert.IsType<StackPanel>(root);
            var window = new Window { Content = panel };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.Equal(42d, Assert.Single(panel.Children).Width);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("TextBlock[Text=&quot;quoted&quot;]", "&quot;quoted&quot;")]
    [InlineData("TextBlock[Text= spaced ]", " spaced ")]
    [InlineData("TextBlock[Text={x:Null}]", "{x:Null}")]
    [InlineData("TextBlock[Text={}literal]", "{}literal")]
    public void PropertySelectorTextRetainsQuotesAndWhitespace(string selector, string value)
    {
        var xaml = "<StackPanel " + Ns + "><StackPanel.Styles><Style Selector='" + selector + "'><Setter Property='Width' Value='42'/></Style></StackPanel.Styles><TextBlock Text='" + (value.StartsWith('{') ? "{}" : "") + value + "'/></StackPanel>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        foreach (var root in new[] { expected.Root, new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml") })
        {
            var panel = Assert.IsType<StackPanel>(root);
            var window = new Window { Content = panel };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.Equal(42d, Assert.Single(panel.Children).Width);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("^Button")]
    [InlineData(".accent^")]
    [InlineData("^:is(Button)")]
    public void NestedSelectorsRetainTheirSupportedCompoundOrder(string selector)
    {
        var xaml = "<Style " + Ns + " Selector='Button'><Style Selector='" + selector + "'/></Style>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(Assert.IsType<Style>(Assert.Single(Assert.IsType<Style>(expected.Root).Children)).Selector!.ToString(), Assert.IsType<Style>(Assert.Single(actual.Children)).Selector!.ToString());
    }

    [AvaloniaFact]
    public void IncompleteTemplateTraversalRetainsTheRuntimeValidationFailure()
    {
        var xaml = "<Style " + Ns + " Selector='Button /template/'/>";
        Assert.IsType<InvalidOperationException>(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var error = Assert.Throws<TargetInvocationException>(() => new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyAttributesStillCreateASelectorlessStyle(string text)
    {
        var xaml = "<Style " + Ns + " Selector='" + text + "'/>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        Assert.Null(Assert.IsType<Style>(expected.Root).Selector);
        Assert.Null(Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml")).Selector);
    }

    [AvaloniaFact]
    public void WhitespacePropertyTextStillCreatesASelectorlessStyle()
    {
        var xaml = "<Style " + Ns + "><Style.Selector> </Style.Selector></Style>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        Assert.Null(Assert.IsType<Style>(expected.Root).Selector);
        Assert.Null(Assert.IsType<Style>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml")).Selector);
    }
}

public static class SelectorContractValues
{
    public static Selector Button => default(Selector)!.OfType<Button>();
}

public readonly record struct SelectorToken(int Value)
{
    public static SelectorToken Parse(string value) => value == "enabled" ? new SelectorToken(7) : throw new FormatException();
}

public sealed class SelectorPropertyControl : Control
{
    public static readonly StyledProperty<int> UnwrappedProperty = AvaloniaProperty.Register<SelectorPropertyControl, int>("Unwrapped");
    public static readonly StyledProperty<SelectorToken> TokenProperty = AvaloniaProperty.Register<SelectorPropertyControl, SelectorToken>(nameof(Token));
    public static readonly StyledProperty<object> DifferentProperty = AvaloniaProperty.Register<SelectorPropertyControl, object>(nameof(Different), 7);
    public SelectorPropertyControl() => SetValue(TokenProperty, new SelectorToken(7));
    [TypeConverter(typeof(SelectorRejectedMemberConverter))]
    public SelectorToken Token { get => GetValue(TokenProperty); set => SetValue(TokenProperty, value); }
    public int Different { get => (int)GetValue(DifferentProperty); set => SetValue(DifferentProperty, value); }
}

public sealed class SelectorRejectedMemberConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => throw new InvalidOperationException("Property converter must not run.");
}
