using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RemainingScopeTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("<TextBlock Text='{CompiledBinding $parent.Tag}'/>")]
    [InlineData("<TextBlock Text='{CompiledBinding $parent[Control].Tag}'/>")]
    [InlineData("<Border><TextBlock Text='{CompiledBinding $parent.Tag}'/></Border>")]
    [InlineData("<Border><TextBlock Text='{CompiledBinding $parent[1].Tag}'/></Border>")]
    [InlineData("<Border><TextBlock Text='{CompiledBinding $parent[Control,1].Tag}'/></Border>")]
    [InlineData("<Border><TextBlock Text='{CompiledBinding $parent[Control;1].Tag}'/></Border>")]
    [InlineData("<Border><StackPanel><TextBlock Text='{CompiledBinding $parent[1].Tag}'/></StackPanel></Border>")]
    [InlineData("<Border x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding $parent.DataContext.Name}'/></Border>")]
    [InlineData("<Border x:DataType='t:BindingFixtureModel'><Border><TextBlock Text='{CompiledBinding $parent.DataContext.Name}'/></Border></Border>")]
    [InlineData("<Border><Border x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding $parent[1].DataContext.Name}'/></Border></Border>")]
    [InlineData("<Border><Border x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding $parent.DataContext.Name}'/></Border></Border>")]
    [InlineData("<ContentControl x:DataType='t:BindingFixtureModel'><ContentControl.ContentTemplate><DataTemplate><TextBlock Text='{CompiledBinding $parent.DataContext.Name}'/></DataTemplate></ContentControl.ContentTemplate></ContentControl>")]
    [InlineData("<ContentControl x:DataType='t:BindingFixtureModel'><ContentControl.ContentTemplate><DataTemplate><Border><TextBlock Text='{CompiledBinding $parent.DataContext.Name}'/></Border></DataTemplate></ContentControl.ContentTemplate></ContentControl>")]
    [InlineData("<Border><t:AncestorBindingHolder Value='{CompiledBinding $parent.Tag}'/></Border>")]
    [InlineData("<Border><Border><t:AncestorBindingHolder Value='{CompiledBinding $parent.Tag}'/></Border></Border>")]
    [InlineData("<TextBlock Text='{CompiledBinding Tag, RelativeSource={RelativeSource TemplatedParent}}'/>")]
    [InlineData("<ControlTemplate TargetType='Button'><TextBlock Text='{CompiledBinding Content, RelativeSource={RelativeSource TemplatedParent}}'/></ControlTemplate>")]
    public void AncestorSourcesFollowTheConstructedTreeAndMetadataScopes(string body) => Compare(body);

    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void DetachedSetterTemplatesRequireTheDirectContentWrapper(bool propertyElement, bool compiled, bool theme)
    {
        var binding = compiled ? "{CompiledBinding CornerRadius, RelativeSource={RelativeSource TemplatedParent}}" : "{TemplateBinding CornerRadius}";
        var template = "<Template><Border CornerRadius='" + binding + "'/></Template>";
        var value = propertyElement ? "<Setter.Value>" + template + "</Setter.Value>" : template;
        Compare(theme ? "<ControlTheme TargetType='ContentControl'><Setter Property='Content'>" + value + "</Setter></ControlTheme>" :
            "<Style Selector='ContentControl'><Setter Property='Content'>" + value + "</Setter></Style>");
    }

    [AvaloniaFact]
    public void DetachedTemplateBindingsFollowTheirMaterializedTemplatedParent()
    {
        var xaml = "<Window " + Ns + "><Window.Resources><ControlTheme x:Key='theme' TargetType='ContentControl'>" +
            "<Setter Property='CornerRadius' Value='10,0,0,10'/><Setter Property='Content'><Template><Border CornerRadius='{TemplateBinding CornerRadius}'/></Template></Setter>" +
            "<Setter Property='Template'><ControlTemplate><Button Content='{TemplateBinding Content}'/></ControlTemplate></Setter>" +
            "</ControlTheme></Window.Resources><ContentControl Theme='{StaticResource theme}'/></Window>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        foreach (var window in new[] { Assert.IsType<Window>(baseline.Root), Assert.IsType<Window>(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Build("Scope.axaml")) })
        {
            try
            {
                window.Show(); window.UpdateLayout();
                var control = Assert.IsType<ContentControl>(window.Content);
                Assert.Equal(new global::Avalonia.CornerRadius(10, 0, 0, 10), Assert.IsType<Border>(control.Content).CornerRadius);
                control.CornerRadius = new global::Avalonia.CornerRadius(7);
                Assert.Equal(control.CornerRadius, Assert.IsType<Border>(control.Content).CornerRadius);
            }
            finally { window.Close(); }
        }
    }

    private static void Compare(string body)
    {
        var end = body.IndexOf('>');
        if (body[end - 1] == '/') end--;
        var xaml = body.Insert(end, " " + Ns);
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        var native = new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) });
        Assert.True((baseline.Error == null) == native.Result.Success,
            "Baseline: " + baseline.Error + "\nNative: " + string.Join("\n", native.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
        if (baseline.Error == null) native.Build("Scope.axaml");
    }
}

public sealed class AncestorBindingHolder : Control
{
    [AssignBinding] public BindingBase? Value { get; set; }
}
