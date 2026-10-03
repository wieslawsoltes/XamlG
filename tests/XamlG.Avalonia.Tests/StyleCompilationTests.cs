using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Frameworks.Avalonia;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class StyleCompilationTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [AvaloniaFact]
    public void CompiledStyleAssignsTypedValuesAndClasses()
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Ns + "><StackPanel.Styles><Style Selector='Button.accent'><Setter Value='144' Property='Width'/><Setter Property='Background' Value='#336699'/></Style></StackPanel.Styles><Button Classes='accent primary' Content='Styled'/></StackPanel>");
        var button = Assert.IsType<Button>(root.Children[0]);
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(144d, button.Width);
            Assert.Equal(Color.Parse("#336699"), Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            Assert.Contains("accent", button.Classes); Assert.Contains("primary", button.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("Button")]
    [InlineData(":is(Button).accent:not(:disabled)")]
    [InlineData("StackPanel > Button:nth-child(2n+1)")]
    [InlineData("StackPanel Button:nth-last-child(-n+3)")]
    [InlineData("Button /template/ ContentPresenter")]
    [InlineData("Button[IsDefault=True]")]
    [InlineData("Button[(Grid.Row)=2]")]
    [InlineData("Button, ToggleButton")]
    public void EmitsSelectorApiCalls(string selector)
    {
        var style = (Style)AvaloniaCompilation.Build("<Style " + Ns + " Selector='" + selector + "'/>");
        Assert.NotNull(style.Selector);
        Assert.NotEmpty(style.Selector.ToString());
    }

    [AvaloniaFact]
    public void NestedStyleAndControlThemePropagateTheTargetType()
    {
        var theme = (ControlTheme)AvaloniaCompilation.Build("<ControlTheme " + Ns + " TargetType='{x:Type Button}'><Setter Property='Width' Value='55'/><Style Selector='^:pointerover'><Setter Property='Width' Value='66'/></Style></ControlTheme>");
        Assert.Equal(typeof(Button), theme.TargetType);
        Assert.Equal(55d, Assert.IsType<Setter>(theme.Setters[0]).Value);
        Assert.Equal(66d, Assert.IsType<Setter>(Assert.IsType<Style>(theme.Children[0]).Setters[0]).Value);
    }

    [Fact]
    public void SetterDiagnosticsAreProducedByTheCompiler()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("StyleDiagnostic", references: references, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var test in new[]
        {
            ("<Style " + Ns + " Selector='Button'><Setter Property='Width' Value='not-a-number'/></Style>", "XG1008"),
            ("<Style " + Ns + " Selector='Button'><Setter Property='Missing' Value='1'/></Style>", "XG3103"),
            ("<Style " + Ns + " Selector='Button'><Setter Property='Width' Value='1'/><Setter Property='Width' Value='2'/></Style>", "XG3107")
        })
        {
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(test.Item1), compilation, AvaloniaFrameworkProfile.Create());
            Assert.Contains(bound.Diagnostics, d => d.Code == test.Item2);
            Assert.False(new CSharpEmitter().Emit(bound).Success);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Button >")]
    [InlineData("Button,,TextBlock")]
    [InlineData("Button:not(")]
    [InlineData("Button:nth-child(2foo)")]
    [InlineData("Button[Tag]")]
    [InlineData("Button:unknown(x)")]
    public void InvalidSelectorsProduceLocatedDiagnostics(string source)
    {
        var diagnostics = new List<XamlDiagnostic>();
        Assert.Null(AvaloniaSelectorParser.Parse(source, new(20, source.Length), diagnostics.Add));
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.InRange(d.Span.Start, 20, 20 + source.Length));
    }
}
