using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DataContextInferenceTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("DataContext='{CompiledBinding Name}' Text='{CompiledBinding Length}'", "")]
    [InlineData("Text='{CompiledBinding Length}' DataContext='{CompiledBinding Name}'", "")]
    [InlineData("DataContext='{Binding Name}' Text='{Binding Length}'", "")]
    [InlineData("Text='{CompiledBinding Length}'", "<TextBlock.DataContext><CompiledBindingExtension Path='Name'/></TextBlock.DataContext>")]
    [InlineData("Text='{CompiledBinding Length}'", "<TextBlock.DataContext><Binding Path='Name'/></TextBlock.DataContext>")]
    [InlineData("DataContext='{CompiledBinding Name}' x:DataType='x:String' Text='{CompiledBinding Length}'", "")]
    [InlineData("DataContext='{CompiledBinding Name, DataType=x:String}' Text='{CompiledBinding Length}'", "")]
    public void ADataContextBindingUsesItsParentTypeAndEstablishesItsResultType(string attributes, string content)
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock " + attributes + ">" + content + "</TextBlock></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new BindingFixtureModel { Name = "first" };
            root.DataContext = model;
            var text = Assert.IsType<TextBlock>(Assert.Single(root.Children));
            Assert.Equal("5", text.Text);
            Assert.Equal("first", text.DataContext);
            model.Name = "updated";
            Assert.Equal("7", text.Text);
        }
    }

    [AvaloniaFact]
    public void DataContextInferenceRequiresAnInheritedTypeEvenWithABindingDataType()
    {
        var xaml = "<TextBlock " + Ns + " DataContext='{CompiledBinding Name, DataType=t:BindingFixtureModel}' Text='{CompiledBinding Length}'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Result.Success);
    }

    [AvaloniaFact]
    public void DataContextResultsFlowThroughNestedControls()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><StackPanel DataContext='{CompiledBinding Child}'><StackPanel DataContext='{CompiledBinding Name}'><TextBlock Text='{CompiledBinding Length}'/></StackPanel></StackPanel></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var child = new BindingFixtureModel { Name = "nested" };
            var model = new BindingFixtureModel { Child = child };
            root.DataContext = model;
            var outer = Assert.IsType<StackPanel>(Assert.Single(root.Children));
            var inner = Assert.IsType<StackPanel>(Assert.Single(outer.Children));
            var text = Assert.IsType<TextBlock>(Assert.Single(inner.Children));
            Assert.Equal("6", text.Text);
            child.Name = "replacement";
            Assert.Equal("11", text.Text);
            model.Child = new BindingFixtureModel { Name = "new" };
            Assert.Equal("3", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("DataContext='{ReflectionBinding Name}'", "")]
    [InlineData("x:CompileBindings='False' DataContext='{Binding Name}'", "")]
    [InlineData("", "<ContentControl.DataContext><ReflectionBindingExtension Path='Name'/></ContentControl.DataContext>")]
    public void ReflectionDataContextsDoNotReuseTheInheritedCompiledType(string attributes, string content)
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><ContentControl " + attributes + ">" + content + "<TextBlock Text='{CompiledBinding Name}'/></ContentControl></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3209");
    }

    [AvaloniaFact]
    public void AnExplicitDirectiveCanTypeAReflectionDataContext()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock DataContext='{ReflectionBinding Name}' x:DataType='x:String' Text='{CompiledBinding Length}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            root.DataContext = new BindingFixtureModel { Name = "explicit" };
            Assert.Equal("8", Assert.IsType<TextBlock>(Assert.Single(root.Children)).Text);
        }
    }

    [AvaloniaFact]
    public void ArrayDataContextsRetainTheirElementType()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock DataContext='{CompiledBinding Numbers}' Text='{CompiledBinding [1]}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            root.DataContext = new BindingFixtureModel();
            Assert.Equal("11", Assert.IsType<TextBlock>(Assert.Single(root.Children)).Text);
        }
    }

    [AvaloniaFact]
    public void InferenceAndAssignmentShareOneConstructedSource()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:CountedBindingSource'><TextBlock Text='{CompiledBinding Length}'><TextBlock.DataContext><CompiledBindingExtension Path='Value'><CompiledBindingExtension.Source><t:CountedBindingSource Value='single'/></CompiledBindingExtension.Source></CompiledBindingExtension></TextBlock.DataContext></TextBlock></StackPanel>";
        foreach (var upstream in new[] { true, false })
        {
            CountedBindingSource.Constructions = 0;
            object? instance;
            if (upstream)
            {
                var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
                Assert.Null(baseline.Error);
                instance = baseline.Root;
            }
            else instance = new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Build("Scope.axaml");
            var root = Assert.IsType<StackPanel>(instance);
            Assert.Equal(1, CountedBindingSource.Constructions);
            Assert.Equal("6", Assert.IsType<TextBlock>(Assert.Single(root.Children)).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("{CompiledBinding Missing}", "XG3205")]
    [InlineData("{CompiledBinding Name, Path=Name}", "XG3210")]
    public void FailedInferenceDoesNotReportTheSameBindingTwice(string binding, string code)
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock DataContext='" + binding + "'/></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Single(project.Result.Documents.Single().Output.Diagnostics.Where(diagnostic => diagnostic.Code == code));
    }

    [AvaloniaTheory]
    [InlineData("{CompiledBinding}")]
    [InlineData("{CompiledBinding Name}")]
    public void ADataContextBindingCannotUseItsOwnControlsDirectiveAsItsInputType(string binding)
    {
        var xaml = "<TextBlock " + Ns + " x:DataType='t:BindingFixtureModel' DataContext='" + binding + "'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Result.Success);
    }

    [AvaloniaFact]
    public void ASelfQualifiedDataContextDoesNotRequireAnInheritedType()
    {
        var xaml = "<TextBlock " + Ns + " DataContext='{CompiledBinding $self}' Text='{CompiledBinding Tag}' Tag='self context'/>";
        foreach (var text in CompileBoth<TextBlock>(xaml))
        {
            Assert.Same(text, text.DataContext);
            Assert.Equal("self context", text.Text);
        }
    }

    [AvaloniaFact]
    public void IgnoredBindingDataTypesAreStillValidated()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock DataContext='{CompiledBinding Name, DataType=t:MissingType}'/></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Result.Success);
    }

    [AvaloniaFact]
    public void ObjectDataContextInferenceExtendsThePinnedCompiler()
    {
        var xaml = "<TextBlock " + Ns + " Text='{CompiledBinding Name}'><TextBlock.DataContext><t:BindingFixtureModel Name='object value'/></TextBlock.DataContext></TextBlock>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var text = Assert.IsType<TextBlock>(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Build("Scope.axaml"));
        Assert.Equal("object value", text.Text);
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsType<T>(baseline.Root);
        yield return Assert.IsType<T>(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Build("Scope.axaml"));
    }
}

public sealed class CountedBindingSource
{
    public static int Constructions { get; set; }
    public CountedBindingSource() => Constructions++;
    public string? Value { get; set; }
}
