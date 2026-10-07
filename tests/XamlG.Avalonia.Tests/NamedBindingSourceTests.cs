using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class NamedBindingSourceTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("Name", false, false)]
    [InlineData("Name", true, false)]
    [InlineData("Name", false, true)]
    [InlineData("Name", true, true)]
    [InlineData("x:Name", false, false)]
    [InlineData("x:Name", true, false)]
    [InlineData("x:Name", false, true)]
    [InlineData("x:Name", true, true)]
    public void LiteralNamesResolveInBothBindingFormsAndDeclarationOrders(string name, bool forward, bool longForm)
    {
        var source = "<TextBox " + name + "='source' Text='first'/>";
        var target = "<TextBlock Text='{CompiledBinding " + (longForm ? "Text, ElementName=source" : "#source.Text") + "}'/>";
        var xaml = "<StackPanel " + Ns + ">" + (forward ? target + source : source + target) + "</StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var input = Assert.Single(root.Children.OfType<TextBox>());
            var text = Assert.Single(root.Children.OfType<TextBlock>());
            Assert.Equal("first", text.Text);
            input.Text = "second";
            Assert.Equal("second", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("TextBox.Name='source'", "")]
    [InlineData("", "<TextBox.Name>source</TextBox.Name>")]
    public void QualifiedAndPropertyElementNamesRegisterBindingSources(string attribute, string content)
    {
        var xaml = "<StackPanel " + Ns + "><TextBlock Text='{CompiledBinding #source.Text}'/><TextBox " + attribute + " Text='named'>" + content + "</TextBox></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
            Assert.Equal("named", Assert.IsType<TextBlock>(root.Children[0]).Text);
    }

    [AvaloniaFact]
    public void NamedInterfaceObjectsCanSupplyBindings()
    {
        var xaml = "<StackPanel " + Ns + "><StackPanel.Resources><t:NamedBindingObject x:Key='value' Name='source' Value='object name'/></StackPanel.Resources><TextBlock Text='{CompiledBinding #source.Value}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
            Assert.Equal("object name", Assert.IsType<TextBlock>(Assert.Single(root.Children)).Text);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedSourcesUseInferredDataContextTypes(bool forward)
    {
        var source = "<TextBlock x:Name='source' DataContext='{CompiledBinding Name}'/>";
        var target = "<TextBlock Text='{CompiledBinding #source.DataContext.Length}'/>";
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'>" + (forward ? target + source : source + target) + "</StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new BindingFixtureModel { Name = "first" };
            root.DataContext = model;
            var text = Assert.IsType<TextBlock>(root.Children[forward ? 0 : 1]);
            Assert.Equal("5", text.Text);
            model.Name = "replacement";
            Assert.Equal("11", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedSourcesRetainArrayDataContextTypes(bool forward)
    {
        var source = "<Control x:Name='source' DataContext='{CompiledBinding Numbers}'/>";
        var target = "<TextBlock Text='{CompiledBinding #source.DataContext[1]}'/>";
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'>" + (forward ? target + source : source + target) + "</StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            root.DataContext = new BindingFixtureModel();
            Assert.Equal("11", Assert.IsType<TextBlock>(root.Children[forward ? 0 : 1]).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InferredNamedSourcesConstructTheirBindingSourcesOnce(bool forward)
    {
        var source = "<Control x:Name='source'><Control.DataContext><CompiledBindingExtension Path='Value'><CompiledBindingExtension.Source><t:CountedBindingSource Value='single'/></CompiledBindingExtension.Source></CompiledBindingExtension></Control.DataContext></Control>";
        var target = "<TextBlock Text='{CompiledBinding #source.DataContext.Length}'/>";
        var xaml = "<StackPanel " + Ns + " x:DataType='t:CountedBindingSource'>" + (forward ? target + source : source + target) + "</StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            Assert.Equal("6", Assert.IsType<TextBlock>(root.Children[forward ? 0 : 1]).Text);
            Assert.Equal(1, CountedBindingSource.Constructions);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReflectionDataContextsDoNotLeakInheritedTypesThroughNames(bool forward)
    {
        var source = "<Control x:Name='source' DataContext='{ReflectionBinding Name}'/>";
        var target = "<TextBlock Text='{CompiledBinding #source.DataContext.Name}'/>";
        Reject("<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'>" + (forward ? target + source : source + target) + "</StackPanel>");
    }

    [AvaloniaFact]
    public void TemplateNamesAreHiddenFromTheOuterScope()
    {
        Reject("<StackPanel " + Ns + "><StackPanel.Resources><ControlTemplate x:Key='nested' TargetType='Button'><TextBox x:Name='privateSource'/></ControlTemplate></StackPanel.Resources><TextBlock Text='{CompiledBinding #privateSource.Text}'/></StackPanel>");
    }

    [AvaloniaFact]
    public void SiblingTemplateNamesDoNotSupplyBindingTypes()
    {
        Reject("<ControlTemplate " + Ns + " TargetType='Button'><StackPanel><StackPanel.Resources><DataTemplate x:Key='nested'><TextBox x:Name='privateSource'/></DataTemplate></StackPanel.Resources><TextBlock Text='{CompiledBinding #privateSource.Text}'/></StackPanel></ControlTemplate>");
    }

    [AvaloniaFact]
    public void TemplateLookupSkipsNestedTemplatesWithTheSameName()
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><StackPanel><StackPanel.Resources><DataTemplate x:Key='nested'><Border x:Name='source'/></DataTemplate></StackPanel.Resources><TextBlock Text='{CompiledBinding #source.Text}'/><TextBox Name='source' Text='visible'/></StackPanel></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml))
        {
            var root = Assert.IsType<StackPanel>(template.Build(new Button())!.Result);
            Assert.Equal("visible", Assert.IsType<TextBlock>(root.Children[0]).Text);
        }
    }

    [AvaloniaFact]
    public void TemplateBindingsCanResolveNamesFromTheRootScope()
    {
        var xaml = "<StackPanel " + Ns + "><TextBox Name='source' Text='outer'/><ContentControl><ContentControl.ContentTemplate><DataTemplate><TextBlock Text='{CompiledBinding #source.Text}'/></DataTemplate></ContentControl.ContentTemplate></ContentControl></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var owner = Assert.IsType<ContentControl>(root.Children[1]);
            var text = Assert.IsType<TextBlock>(owner.ContentTemplate!.Build("item"));
            Assert.Equal("outer", text.Text);
            Assert.IsType<TextBox>(root.Children[0]).Text = "changed";
            Assert.Equal("changed", text.Text);
        }
    }

    [AvaloniaFact]
    public void RootScopeTypesHaveUpstreamPrecedenceOverTemplateNames()
    {
        Reject("<StackPanel " + Ns + " Name='source'><ContentControl><ContentControl.ContentTemplate><DataTemplate><StackPanel><TextBox x:Name='source' Text='inner'/><TextBlock Text='{CompiledBinding #source.Text}'/></StackPanel></DataTemplate></ContentControl.ContentTemplate></ContentControl></StackPanel>");
    }

    [AvaloniaFact]
    public void NestedTemplatesCanResolveAnEnclosingTemplateName()
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><StackPanel><TextBox Name='source' Text='enclosing'/><ContentControl><ContentControl.ContentTemplate><DataTemplate><TextBlock Text='{CompiledBinding #source.Text}'/></DataTemplate></ContentControl.ContentTemplate></ContentControl></StackPanel></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml))
        {
            var root = Assert.IsType<StackPanel>(template.Build(new Button())!.Result);
            var owner = Assert.IsType<ContentControl>(root.Children[1]);
            Assert.Equal("enclosing", Assert.IsType<TextBlock>(owner.ContentTemplate!.Build("item")).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("ModelType='x:String'", "")]
    [InlineData("", "<t:DataTypeHost.ModelType><x:Type TypeName='x:String'/></t:DataTypeHost.ModelType>")]
    public void NamedSourcesHonorAnnotatedDataTypeProperties(string attribute, string content)
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding #source.DataContext.Length}'/><t:DataTypeHost Name='source' " + attribute + ">" + content + "</t:DataTypeHost></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            root.Children[1].DataContext = "annotated";
            Assert.Equal("9", Assert.IsType<TextBlock>(root.Children[0]).Text);
        }
    }

    [AvaloniaFact]
    public void ForwardSourceMetadataCanComeFromAnInferredParent()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding #source.DataContext.Length}'/><ContentControl DataContext='{CompiledBinding Name}'><Control x:Name='source'/></ContentControl></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new BindingFixtureModel { Name = "parent" };
            root.DataContext = model;
            Assert.Equal("6", Assert.IsType<TextBlock>(root.Children[0]).Text);
            model.Name = "updated";
            Assert.Equal("7", Assert.IsType<TextBlock>(root.Children[0]).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedDataContextInferenceObservesOnlyEarlierMetadata(bool forward)
    {
        var source = "<Control x:Name='source' DataContext='{CompiledBinding Name}'/>";
        var target = "<Control DataContext='{CompiledBinding #source.DataContext.Length}'/>";
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'>" + (forward ? target + source : source + target) + "</StackPanel>";
        if (forward) { Reject(xaml); return; }
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            root.DataContext = new BindingFixtureModel { Name = "phase" };
            Assert.Equal(5, root.Children[1].DataContext);
        }
    }

    [AvaloniaFact]
    public void FutureDirectivesDoNotTypeAnEarlierDataContextBinding()
    {
        Reject("<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><Control DataContext='{CompiledBinding #source.DataContext.Length}'/><Control Name='source' x:DataType='x:String'/></StackPanel>");
    }

    [AvaloniaFact]
    public void ANameLookupDoesNotImportDataTypeMetadataOutsideItsTemplateScope()
    {
        Reject("<DataTemplate " + Ns + " DataType='t:BindingFixtureModel'><StackPanel><Control x:Name='source'/><TextBlock Text='{CompiledBinding #source.DataContext.Name}'/></StackPanel></DataTemplate>");
    }

    [AvaloniaFact]
    public void MetadataInsideTheTemplateScopeCanTypeANamedSource()
    {
        var xaml = "<DataTemplate " + Ns + "><StackPanel x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding #source.DataContext.Name}'/><Control Name='source'/></StackPanel></DataTemplate>";
        foreach (var template in CompileBoth<IDataTemplate>(xaml))
        {
            var root = Assert.IsType<StackPanel>(template.Build(null));
            root.DataContext = new BindingFixtureModel { Name = "template scope" };
            Assert.Equal("template scope", Assert.IsType<TextBlock>(root.Children[0]).Text);
        }
    }

    [AvaloniaFact]
    public void FailedForwardInferenceDoesNotDuplicateTheSourceDiagnostic()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding #source.Tag}'/><Control Name='source' DataContext='{CompiledBinding Missing}'/></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Named.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Single(project.Result.Documents.Single().Output.Diagnostics.Where(diagnostic => diagnostic.Code == "XG3205"));
    }

    [AvaloniaFact]
    public void NamedSourcesRetainTypesInferredFromCollectionMetadata()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:ItemSourceFixtureModel'><TextBlock Text='{CompiledBinding #source.DataContext.Name}'/><t:NamedItemHost ItemsSource='{CompiledBinding Rows}'><t:NamedItemHost.Item><Control Name='source'/></t:NamedItemHost.Item></t:NamedItemHost></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new ItemSourceFixtureModel();
            root.DataContext = model;
            Assert.IsType<NamedItemHost>(root.Children[1]).Item!.DataContext = model.Rows[0];
            Assert.Equal("collection item", Assert.IsType<TextBlock>(root.Children[0]).Text);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ItemInferenceResolvesNamedSourcesDuringTheMetadataPass(bool forward, bool declaredTemplateType)
    {
        var source = "<Control Name='source' DataContext='{CompiledBinding Rows}'/>";
        var target = "<ItemsControl ItemsSource='{CompiledBinding #source.DataContext}'><ItemsControl.ItemTemplate><DataTemplate" +
            (declaredTemplateType ? " DataType='t:BindingFixtureModel'" : "") + "><TextBlock Text='{CompiledBinding Name}'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>";
        var xaml = "<StackPanel " + Ns + " x:DataType='t:ItemSourceFixtureModel'>" + (forward ? target + source : source + target) + "</StackPanel>";
        if (forward && !declaredTemplateType) { Reject(xaml); return; }
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new ItemSourceFixtureModel();
            root.DataContext = model;
            var items = Assert.Single(root.Children.OfType<ItemsControl>());
            Assert.Same(model.Rows, items.ItemsSource);
            var text = Assert.IsType<TextBlock>(items.ItemTemplate!.Build(model.Rows[0]));
            text.DataContext = model.Rows[0];
            Assert.Equal("collection item", text.Text);
        }
    }

    [AvaloniaFact]
    public void InlineItemBindingsCanUseTheirOwnersNamedDataContext()
    {
        var xaml = "<ItemsControl " + Ns + " Name='owner' x:DataType='t:ItemSourceFixtureModel' ItemsSource='{CompiledBinding #owner.DataContext.Rows}' DisplayMemberBinding='{CompiledBinding Name}'/>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            var model = new ItemSourceFixtureModel();
            control.DataContext = model;
            Assert.Same(model.Rows, control.ItemsSource);
            var text = new TextBlock { DataContext = model.Rows[0] };
            using var binding = text.Bind(TextBlock.TextProperty, control.DisplayMemberBinding!);
            Assert.Equal("collection item", text.Text);
        }
    }

    private static void Reject(string xaml)
    {
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Named.axaml", xaml) }).Result.Success);
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        CountedBindingSource.Constructions = 0;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        CountedBindingSource.Constructions = 0;
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Named.axaml", xaml) }).Build("Named.axaml"));
    }
}

public sealed class NamedBindingObject : global::Avalonia.INamed
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

public sealed class NamedItemHost : ItemsControl
{
    [global::Avalonia.Metadata.InheritDataTypeFromItems(nameof(ItemsSource))]
    public Control? Item { get; set; }
}
