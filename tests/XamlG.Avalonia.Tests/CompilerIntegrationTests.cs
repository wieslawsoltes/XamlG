using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlG.AvaloniaRuntime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class CompilerIntegrationTests
{
    private const string Namespace = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [AvaloniaFact]
    public void BuildsControlsWithRealNamescopeAndBrushConversion()
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Namespace + " Spacing='8'><Border Background='#336699' Padding='12'><TextBlock x:Name='title' Text='Compiled'/></Border></StackPanel>");
        Assert.Equal(8, root.Spacing);
        Assert.Equal("Compiled", root.FindControl<TextBlock>("title")!.Text);
        var border = Assert.IsType<Border>(Assert.Single(root.Children));
        Assert.Equal(Color.Parse("#336699"), ((ISolidColorBrush)border.Background!).Color);
    }

    [AvaloniaFact]
    public void BindingIsAppliedToTheActualAvaloniaProperty()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Namespace + " Text='{Binding}'/>");
        root.DataContext = "bound value";
        Assert.Equal("bound value", root.Text);
    }

    [AvaloniaFact]
    public void StaticResourcesUseTheGeneratedParentStack()
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Namespace + "><StackPanel.Resources><SolidColorBrush x:Key='accent' Color='Red'/></StackPanel.Resources><Border Background='{StaticResource accent}'/></StackPanel>");
        Assert.Equal(Colors.Red, ((ISolidColorBrush)((Border)root.Children[0]).Background!).Color);
    }

    [AvaloniaFact]
    public void DeferredDataTemplateCreatesIndependentControlTrees()
    {
        var template = (DataTemplate)AvaloniaCompilation.Build("<DataTemplate " + Namespace + "><TextBlock Text='Template instance'/></DataTemplate>");
        var first = template.Build(null);
        var second = template.Build(null);
        Assert.NotSame(first, second);
        Assert.Equal("Template instance", Assert.IsType<TextBlock>(first).Text);
    }

    [AvaloniaFact]
    public void VisualInspectorIncludesRealizedControlTemplateChildren()
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Namespace + " Content='Inspect me'/>");
        var window = new Window { Content = root, Width = 400, Height = 240 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var tree = AvaloniaVisualInspector.Inspect(root);
            Assert.NotEmpty(tree.Children);
            Assert.Same(root, AvaloniaVisualInspector.Find(root, "visual"));
        }
        finally { window.Close(); }
    }
}
