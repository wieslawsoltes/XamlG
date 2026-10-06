using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class TemplateOwnerRegressionTests
{
    [AvaloniaFact]
    public void ThemeSetterWithoutConcreteTemplateUsesThemeOwner()
    {
        var theme = Build("<Setter Property='CornerRadius' Value='{TemplateBinding CornerRadius}'/>");
        var binding = Assert.IsType<TemplateBinding>(Assert.IsType<Setter>(Assert.Single(theme.Setters)).Value);
        Assert.Same(TemplatedControl.CornerRadiusProperty, binding.Property);
    }

    [AvaloniaFact]
    public void TemplateSelectorKeepsSourceOwnerSeparateFromSelectedChild()
    {
        var theme = Build("<Style Selector='^ /template/ RepeatButton'><Setter Property='IsVisible' Value='{TemplateBinding ShowButtonSpinner}'/></Style>");
        var style = Assert.IsType<Style>(Assert.Single(theme.Children));
        var binding = Assert.IsType<TemplateBinding>(Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
        Assert.Same(ButtonSpinner.ShowButtonSpinnerProperty, binding.Property);
    }

    [AvaloniaFact]
    public void NestedPseudoClassInheritsTheTemplateSource()
    {
        var theme = Build("<Style Selector='^ /template/ RepeatButton'><Style Selector='^:pressed'><Setter Property='IsVisible' Value='{TemplateBinding ShowButtonSpinner}'/></Style></Style>");
        var parent = Assert.IsType<Style>(Assert.Single(theme.Children));
        var style = Assert.IsType<Style>(Assert.Single(parent.Children));
        var binding = Assert.IsType<TemplateBinding>(Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
        Assert.Same(ButtonSpinner.ShowButtonSpinnerProperty, binding.Property);
    }

    [AvaloniaFact]
    public void LastTemplateTraversalSelectsTheNearestOwner()
    {
        var theme = Build("<Style Selector='^ /template/ RepeatButton /template/ ContentPresenter'><Setter Property='Content' Value='{TemplateBinding ClickMode}'/></Style>");
        var style = Assert.IsType<Style>(Assert.Single(theme.Children));
        var binding = Assert.IsType<TemplateBinding>(Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
        Assert.Same(Button.ClickModeProperty, binding.Property);
    }

    [Fact]
    public void NestedConcreteTemplateShadowsTheOuterTheme()
    {
        var fixture = Fixture("<Setter Property='Template'><ControlTemplate><Button><Button.Template><ControlTemplate><ContentPresenter Content='{TemplateBinding ClickMode}'/></ControlTemplate></Button.Template></Button></ControlTemplate></Setter>");
        Assert.NotEmpty(fixture.Emit());
    }

    private static ResourceProjectFixture Fixture(string children) => new(new[]
    {
        ("Theme.axaml", "<ControlTheme " + ResourceProjectFixture.Namespace + " TargetType='ButtonSpinner'>" + children + "</ControlTheme>")
    });

    private static ControlTheme Build(string children) => Assert.IsType<ControlTheme>(Fixture(children).Build("Theme.axaml"));
}
