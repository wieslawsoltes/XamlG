using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ThemeLiteralCompatibilityTests
{
    private const string Ns = ResourceProjectFixture.Namespace;

    [AvaloniaTheory]
    [InlineData("{TemplateBinding Padding}")]
    [InlineData("{TemplateBinding Property=Padding}")]
    [InlineData("{TemplateBinding Button.Padding}")]
    public void TemplatePropertyLiteralsUseTheTemplatedParent(string value)
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml", "<Button " + Ns + " Padding='11' Width='160' Height='80'>" +
            "<Button.Template><ControlTemplate><Border x:Name='part' Padding='" + value + "'/></ControlTemplate></Button.Template></Button>") });
        var button = Assert.IsType<Button>(project.Build("View.axaml"));
        var window = new Window { Content = button };
        try
        {
            window.Show(); window.UpdateLayout();
            var part = button.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "part");
            Assert.Equal(new Thickness(11), part.Padding);
            button.Padding = new Thickness(19);
            Assert.Equal(new Thickness(19), part.Padding);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ObjectElementTemplateBindingsResolveTheSameProperty()
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml", "<Button " + Ns + " Padding='7' Width='160' Height='80'>" +
            "<Button.Template><ControlTemplate><Border x:Name='part'><Border.Padding><TemplateBinding Property='Padding'/></Border.Padding>" +
            "</Border></ControlTemplate></Button.Template></Button>") });
        var button = Assert.IsType<Button>(project.Build("View.axaml"));
        var window = new Window { Content = button };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(new Thickness(7), button.GetVisualDescendants().OfType<Border>().Single(control => control.Name == "part").Padding);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReferenceValuedResourceLiteralsUseTheirRealParsers()
    {
        var project = new ResourceProjectFixture(new[] { ("Resources.axaml", ResourceProjectFixture.Dictionary("""
            <SolidColorBrush x:Key="brush">#336699</SolidColorBrush>
            <StreamGeometry x:Key="geometry">M 0,0 L 10,10</StreamGeometry>
            <TransformOperations x:Key="transform">scale(0.9)</TransformOperations>
            <FontFamily x:Key="font">fonts/#Example</FontFamily>
            """)) });
        var root = Assert.IsType<ResourceDictionary>(project.Build("Resources.axaml"));
        Assert.Equal(Color.Parse("#336699"), Assert.IsType<global::Avalonia.Media.Immutable.ImmutableSolidColorBrush>(root["brush"]).Color);
        Assert.IsType<StreamGeometry>(root["geometry"]);
        Assert.IsType<TransformOperations>(root["transform"]);
        var font = Assert.IsType<FontFamily>(root["font"]);
        Assert.Equal("Example", font.Name); Assert.NotNull(font.Key);
        Assert.Contains("BaseUri", project.Result.Documents.Single().Output.Source);
    }

    [AvaloniaFact]
    public void TransitionPropertyLiteralsUseTheStyledTarget()
    {
        var project = new ResourceProjectFixture(new[] { ("Resources.axaml", ResourceProjectFixture.Dictionary("""
            <ControlTheme x:Key="theme" TargetType="Button">
              <Setter Property="Transitions"><Transitions><DoubleTransition Property="Opacity" Duration="0:0:0.1"/></Transitions></Setter>
            </ControlTheme>
            """)) });
        var root = (ResourceDictionary)project.Build("Resources.axaml");
        var theme = Assert.IsType<ControlTheme>(root["theme"]);
        var setter = Assert.IsType<Setter>(Assert.Single(theme.Setters));
        var transition = Assert.IsType<DoubleTransition>(Assert.Single(Assert.IsType<Transitions>(setter.Value)));
        Assert.Same(Visual.OpacityProperty, transition.Property);
    }

    [Fact]
    public void RuntimeCompilationIgnoresDesignValuesButNotRuntimeErrors()
    {
        var preview = new ResourceProjectFixture(new[] { ("Resources.axaml", ResourceProjectFixture.Dictionary(
            "<Design.PreviewWith><Button PropertyOnlyInDesigner='invalid'/></Design.PreviewWith><x:String x:Key='kept'>value</x:String>")) });
        Assert.True(preview.Result.Success, string.Join("\n", preview.Result.Documents.Single().Output.Diagnostics));
        Assert.DoesNotContain("PropertyOnlyInDesigner", preview.Result.Documents.Single().Output.Source);
        var runtime = new ResourceProjectFixture(new[] { ("Resources.axaml", ResourceProjectFixture.Dictionary(
            "<Button x:Key='bad' PropertyOnlyInDesigner='invalid'/>")) });
        Assert.False(runtime.Result.Success);
        Assert.Contains(runtime.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG1005");
    }

    [Fact]
    public void MissingTemplatePropertyProducesAnErrorNotAReflectionFallback()
    {
        var project = new ResourceProjectFixture(new[] { ("View.axaml", "<Button " + Ns + "><Button.Template>" +
            "<ControlTemplate><Border Padding='{TemplateBinding NotAProperty}'/></ControlTemplate></Button.Template></Button>") });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG1008" && diagnostic.Message.Contains("NotAProperty"));
    }

    [Fact]
    public void ReferenceLiteralConversionDoesNotDropOtherMembers()
    {
        const string code = "namespace Model { public sealed class Token { private Token(string text) { Text = text; } public string Text {get;} public string Mode {get;set;} public static Token Parse(string value) => new Token(value); } }";
        var valid = new ResourceProjectFixture(new[] { ("R.axaml", ResourceProjectFixture.Dictionary(
            "<m:Token xmlns:m='clr-namespace:Model' x:Key='token'>hello</m:Token>")) }, sourceCode: code);
        var root = (ResourceDictionary)valid.Build("R.axaml");
        var token = root["token"]!;
        Assert.Equal("hello", token.GetType().GetProperty("Text")!.GetValue(token));
        var invalid = new ResourceProjectFixture(new[] { ("R.axaml", ResourceProjectFixture.Dictionary(
            "<m:Token xmlns:m='clr-namespace:Model' x:Key='token' Mode='must-not-be-lost'>hello</m:Token>")) }, sourceCode: code);
        Assert.False(invalid.Result.Success);
    }
}
