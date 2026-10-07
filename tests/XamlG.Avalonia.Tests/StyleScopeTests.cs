using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Metadata;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class StyleScopeTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("<t:DerivedStyle Selector='Button'><Setter Property='Width' Value='42'/></t:DerivedStyle>")]
    [InlineData("<t:DerivedStyle Style.Selector='Button'><Setter Property='Width' Value='42'/></t:DerivedStyle>")]
    [InlineData("<t:DerivedControlTheme TargetType='{x:Type Button}'><Setter Property='Width' Value='42'/></t:DerivedControlTheme>")]
    [InlineData("<ControlTheme ControlTheme.TargetType='Button'><Setter Property='Width' Value='42'/></ControlTheme>")]
    [InlineData("<ControlTheme><ControlTheme.TargetType><x:Type TypeName='Button'/></ControlTheme.TargetType><Setter Property='Width' Value='42'/></ControlTheme>")]
    public void DerivedStylesAndObjectFormTargetsRetainTheSetterType(string content)
    {
        var xaml = content.Insert(content.IndexOf('>'), " " + Ns);
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsAssignableFrom<StyleBase>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(42d, Assert.IsType<Setter>(Assert.Single(actual.Setters)).Value);
    }

    [AvaloniaTheory]
    [InlineData("t:DerivedControlTemplate")]
    [InlineData("t:InterfaceTemplate")]
    public void CustomTemplateScopesResolveTemplateBindingProperties(string type)
    {
        var xaml = "<" + type + " " + Ns + " TargetType='{x:Type Button}'><Border Padding='{TemplateBinding Padding}'/></" + type + ">";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsAssignableFrom<IControlTemplate>(new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml"));
        Assert.IsType<Border>(actual.Build(new Button())!.Result);
    }

    [AvaloniaTheory]
    [InlineData("<ControlTheme/>")]
    [InlineData("<ControlTheme TargetType='{x:Type Button}'><Style><Setter Property='Width' Value='42'/></Style></ControlTheme>")]
    public void InvalidStyleScopesAreRejectedLikeUpstream(string content)
    {
        var insert = content.IndexOf('>');
        if (content[insert - 1] == '/') insert--;
        var xaml = content.Insert(insert, " " + Ns);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Style.axaml", xaml) });
        Assert.False(project.Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData(" Selector='' ")]
    [InlineData(" Selector='  ' ")]
    public void AStyleWithoutASelectorUsesItsOwningControlType(string selector)
    {
        var xaml = "<Button " + Ns + "><Button.Styles><Style" + selector + "><Setter Property='Width' Value='42'/></Style></Button.Styles></Button>";
        var expected = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(expected.Error);
        var actual = Assert.IsType<Button>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        Assert.Equal(42d, Assert.IsType<Setter>(Assert.IsType<Style>(Assert.Single(actual.Styles)).Setters[0]).Value);
    }

    [AvaloniaFact]
    public void AnExplicitStylesCollectionDoesNotHideTheOwningControl()
    {
        var xaml = "<Button " + Ns + "><Button.Styles><Styles><Style><Setter Property='Width' Value='42'/></Style></Styles></Button.Styles></Button>";
        Assert.Null(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var actual = Assert.IsType<Button>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        var styles = Assert.IsType<Styles>(Assert.Single(actual.Styles));
        Assert.Equal(42d, Assert.IsType<Setter>(Assert.IsType<Style>(Assert.Single(styles)).Setters[0]).Value);
    }

    [AvaloniaTheory]
    [InlineData("Style", "Selector='Border'")]
    [InlineData("ControlTheme", "TargetType='Border'")]
    public void SetterTargetTypeOverridesTheInferredStyleType(string type, string target)
    {
        var xaml = "<" + type + " " + Ns + " " + target + " x:SetterTargetType='Button'><Setter Property='IsDefault' Value='True'/></" + type + ">";
        Assert.Null(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var actual = Assert.IsAssignableFrom<StyleBase>(new ResourceProjectFixture(new[] { ("Style.axaml", xaml) }).Build("Style.axaml"));
        var setter = Assert.IsType<Setter>(Assert.Single(actual.Setters));
        Assert.Equal(Button.IsDefaultProperty, setter.Property);
        Assert.Equal(true, setter.Value);
    }
}

public sealed class DerivedStyle : Style { }
public sealed class DerivedControlTheme : ControlTheme { }
public sealed class DerivedControlTemplate : ControlTemplate { }
public sealed class InterfaceTemplate : IControlTemplate
{
    [Content, TemplateContent] public object? Content { get; set; }
    public Type? TargetType { get; set; }
    public TemplateResult<Control>? Build(TemplatedControl control) => TemplateContent.Load(Content);
}
