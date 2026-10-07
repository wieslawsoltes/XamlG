using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class DataTypeMetadataTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("DataTemplate", "t:BindingFixtureModel")]
    [InlineData("DataTemplate", "{x:Type t:BindingFixtureModel}")]
    [InlineData("TreeDataTemplate", "t:BindingFixtureModel")]
    [InlineData("t:DerivedDataTemplate", "t:BindingFixtureModel")]
    [InlineData("t:CustomTypedTemplate", "t:BindingFixtureModel")]
    [InlineData("t:HiddenDataTypeTemplate", "t:BindingFixtureModel")]
    public void DataTypeDirectivesSetTheAnnotatedPropertyAndCompileTheTemplateBody(string type, string value)
    {
        var xaml = "<" + type + " " + Ns + " x:DataType='" + value + "'><TextBlock Text='{CompiledBinding Name}'/></" + type + ">";
        foreach (var root in CompileBoth(xaml))
        {
            var template = Assert.IsAssignableFrom<IDataTemplate>(root);
            Assert.Equal(typeof(BindingFixtureModel), Assert.IsAssignableFrom<ITypedDataTemplate>(root).DataType);
            var model = new BindingFixtureModel { Name = "typed template" };
            Assert.True(template.Match(model));
            Assert.False(template.Match("wrong type"));
            var control = Assert.IsType<TextBlock>(template.Build(model));
            control.DataContext = model;
            Assert.Equal("typed template", control.Text);
            model.Name = "changed";
            Assert.Equal("changed", control.Text);
            if (root is HiddenDataTypeTemplate hidden) Assert.Null(hidden.DataType);
        }
    }

    [AvaloniaTheory]
    [InlineData("DataTemplate")]
    [InlineData("t:DerivedDataTemplate")]
    [InlineData("t:CustomTypedTemplate")]
    public void AnExplicitDataTypePropertyDoesNotReplaceTheDirectiveBindingScope(string type)
    {
        var xaml = "<" + type + " " + Ns + " DataType='x:String' x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding Name}'/></" + type + ">";
        foreach (var root in CompileBoth(xaml))
        {
            Assert.Equal(typeof(string), Assert.IsAssignableFrom<ITypedDataTemplate>(root).DataType);
            var model = new BindingFixtureModel { Name = "directive scope" };
            var control = Assert.IsType<TextBlock>(Assert.IsAssignableFrom<IDataTemplate>(root).Build(model));
            control.DataContext = model;
            Assert.Equal("directive scope", control.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("DataTemplate", "<DataTemplate.DataType><x:Type TypeName='t:BindingFixtureModel'/></DataTemplate.DataType>")]
    [InlineData("t:DerivedDataTemplate", "<DataTemplate.DataType>t:BindingFixtureModel</DataTemplate.DataType>")]
    [InlineData("t:CustomTypedTemplate", "<t:CustomTypedTemplate.DataType><x:Type TypeName='t:BindingFixtureModel'/></t:CustomTypedTemplate.DataType>")]
    public void AnnotatedPropertyElementsEstablishTheCompiledBindingType(string type, string property)
    {
        var xaml = "<" + type + " " + Ns + ">" + property + "<TextBlock Text='{CompiledBinding Name}'/></" + type + ">";
        foreach (var root in CompileBoth(xaml))
        {
            var control = Assert.IsType<TextBlock>(Assert.IsAssignableFrom<IDataTemplate>(root).Build(null));
            control.DataContext = new BindingFixtureModel { Name = "property element" };
            Assert.Equal("property element", control.Text);
        }
    }

    [AvaloniaFact]
    public void AnnotatedPropertiesWithOtherNamesEstablishTheBindingScope()
    {
        var xaml = "<t:DataTypeHost " + Ns + " ModelType='{x:Type t:BindingFixtureModel}'><TextBlock Text='{CompiledBinding Name}'/></t:DataTypeHost>";
        foreach (var root in CompileBoth(xaml))
        {
            var host = Assert.IsType<DataTypeHost>(root);
            Assert.Equal(typeof(BindingFixtureModel), host.ModelType);
            host.DataContext = new BindingFixtureModel { Name = "annotated property" };
            Assert.Equal("annotated property", Assert.IsType<TextBlock>(host.Content).Text);
        }
    }

    [AvaloniaFact]
    public void TheDirectiveDoesNotAssignAnUnannotatedDataTypeProperty()
    {
        var xaml = "<t:UnannotatedDataTypeHost " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding Name}'/></t:UnannotatedDataTypeHost>";
        foreach (var root in CompileBoth(xaml))
        {
            var host = Assert.IsType<UnannotatedDataTypeHost>(root);
            Assert.Null(host.DataType);
            host.DataContext = new BindingFixtureModel { Name = "compile-time only" };
            Assert.Equal("compile-time only", Assert.IsType<TextBlock>(host.Content).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("DataTemplate")]
    [InlineData("t:DerivedDataTemplate")]
    [InlineData("t:CustomTypedTemplate")]
    public void UntypedTemplatesDoNotInheritTheirOwnersDataContextType(string type)
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><StackPanel.Resources><" + type + " x:Key='Item'><TextBlock Text='{CompiledBinding Name}'/></" + type + "></StackPanel.Resources></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3209");
    }

    [AvaloniaFact]
    public void ACompileTimeDirectiveRequiresAStaticallyKnownType()
    {
        var xaml = "<TextBlock " + Ns + " x:DataType='{x:Null}'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Result.Success);
    }

    private static IEnumerable<object> CompileBoth(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<object>(baseline.Root);
        yield return new ResourceProjectFixture(new[] { ("Scope.axaml", xaml) }).Build("Scope.axaml");
    }
}

public sealed class DerivedDataTemplate : DataTemplate { }
public sealed class HiddenDataTypeTemplate : DataTemplate { public new Type? DataType { get; set; } }
public sealed class CustomTypedTemplate : IDataTemplate, ITypedDataTemplate
{
    [DataType] public Type? DataType { get; set; }
    [Content, TemplateContent] public object? Content { get; set; }
    public bool Match(object? data) => DataType?.IsInstanceOfType(data) ?? true;
    public Control? Build(object? data) => TemplateContent.Load(Content)?.Result;
}
public sealed class DataTypeHost : ContentControl { [DataType] public Type? ModelType { get; set; } }
public sealed class UnannotatedDataTypeHost : ContentControl { public Type? DataType { get; set; } }
