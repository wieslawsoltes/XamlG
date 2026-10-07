using System.Collections;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ItemTypeInferenceTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    private const string Template = "<ItemsControl.ItemTemplate><DataTemplate><TextBlock Text='{CompiledBinding Name}'/></DataTemplate></ItemsControl.ItemTemplate>";

    [AvaloniaTheory]
    [InlineData("{CompiledBinding Rows}")]
    [InlineData("{Binding Rows}")]
    [InlineData("{CompiledBinding InterfaceRows}")]
    [InlineData("{CompiledBinding ArrayRows}")]
    [InlineData("{CompiledBinding Rows, DataType=x:String}")]
    public void CollectionBindingsEstablishTheTemplateItemType(string binding)
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' ItemsSource='" + binding + "'>" + Template + "</ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            control.DataContext = new ItemSourceFixtureModel();
            var item = Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>()));
            AssertTemplate(control.ItemTemplate!, item);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CollectionPropertyElementsCanFollowTheTemplate(bool sourceFirst)
    {
        var source = "<ItemsControl.ItemsSource><CompiledBindingExtension Path='Rows'/></ItemsControl.ItemsSource>";
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel'>" + (sourceFirst ? source + Template : Template + source) + "</ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            control.DataContext = new ItemSourceFixtureModel();
            AssertTemplate(control.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>())));
        }
    }

    [AvaloniaTheory]
    [InlineData("DataTemplate")]
    [InlineData("TreeDataTemplate")]
    [InlineData("t:DerivedDataTemplate")]
    [InlineData("t:CustomTypedTemplate")]
    public void InferenceSupportsAllDataTemplateImplementations(string type)
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' ItemsSource='{CompiledBinding Rows}'><ItemsControl.ItemTemplate><" + type + "><TextBlock Text='{CompiledBinding Name}'/></" + type + "></ItemsControl.ItemTemplate></ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            Assert.Null(Assert.IsAssignableFrom<ITypedDataTemplate>(control.ItemTemplate).DataType);
            AssertTemplate(control.ItemTemplate!, new BindingFixtureModel { Name = "inferred" });
        }
    }

    [AvaloniaTheory]
    [InlineData("ItemsSource='{x:Static t:ItemSourceFixtureValues.Rows}'", "")]
    [InlineData("", "<ItemsControl.ItemsSource><t:ItemFixtureCollection><t:BindingFixtureModel Name='constructed'/></t:ItemFixtureCollection></ItemsControl.ItemsSource>")]
    public void TypedCollectionValuesDoNotRequireADataTypeDirective(string attribute, string source)
    {
        var xaml = "<ItemsControl " + Ns + " " + attribute + ">" + Template + source + "</ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
            AssertTemplate(control.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>())));
    }

    [AvaloniaTheory]
    [InlineData("ItemsSource='{CompiledBinding Rows}' DisplayMemberBinding='{CompiledBinding Name}'", "")]
    [InlineData("DisplayMemberBinding='{CompiledBinding Name}' ItemsSource='{CompiledBinding Rows}'", "")]
    [InlineData("ItemsSource='{CompiledBinding Rows}'", "<ItemsControl.DisplayMemberBinding><CompiledBindingExtension Path='Name'/></ItemsControl.DisplayMemberBinding>")]
    public void AssignedDisplayBindingsUseTheItemType(string attributes, string content)
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' " + attributes + " Tag='{CompiledBinding Rows.Count}'>" + content + "</ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            control.DataContext = new ItemSourceFixtureModel();
            AssertBinding(control.DisplayMemberBinding!);
            Assert.Equal(1, control.Tag);
        }
    }

    [AvaloniaTheory]
    [InlineData("t:ItemInferenceGrid", false, false)]
    [InlineData("t:DerivedItemInferenceGrid", false, false)]
    [InlineData("t:ItemInferenceGrid", true, false)]
    [InlineData("t:ItemInferenceGrid", false, true)]
    [InlineData("t:ItemInferenceGrid", true, true)]
    public void CustomPropertiesCanSelectAnAncestorCollection(string type, bool sourceLast, bool conflictingBindingType)
    {
        var source = "<t:ItemInferenceGrid.Items><CompiledBindingExtension Path='Rows'" + (conflictingBindingType ? " DataType='x:String'" : "") + "/></t:ItemInferenceGrid.Items>";
        var columns = "<t:ItemInferenceGrid.Columns><t:ItemInferenceColumn Binding='{CompiledBinding Name}'><t:ItemInferenceColumn.Template><DataTemplate><TextBlock Text='{CompiledBinding Name}'/></DataTemplate></t:ItemInferenceColumn.Template></t:ItemInferenceColumn></t:ItemInferenceGrid.Columns>";
        var xaml = "<" + type + " " + Ns + " x:DataType='t:ItemSourceFixtureModel'>" + (sourceLast ? columns + source : source + columns) + "</" + type + ">";
        foreach (var control in CompileBoth<Control>(xaml))
        {
            var grid = Assert.IsAssignableFrom<ItemInferenceGrid>(control);
            var column = Assert.Single(grid.Columns);
            AssertBinding(column.Binding!);
            AssertTemplate(column.Template!, new BindingFixtureModel { Name = "column item" });
        }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("ItemsSource='{ReflectionBinding Rows}'")]
    public void UninferrableCollectionsDoNotLeakTheOwnersTypeIntoTemplates(string source)
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' " + source + "><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text='{CompiledBinding Rows}'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var project = new ResourceProjectFixture(new[] { ("Items.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3209");
    }

    [AvaloniaFact]
    public void ExplicitTemplateTypesTakePrecedenceOverCollectionInference()
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' ItemsSource='{CompiledBinding Rows}'><ItemsControl.ItemTemplate><DataTemplate x:DataType='x:String'><TextBlock Text='{CompiledBinding Length}'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            var text = Assert.IsType<TextBlock>(control.ItemTemplate!.Build("explicit"));
            text.DataContext = "explicit";
            Assert.Equal("8", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("x:DataType='t:BindingFixtureModel'")]
    [InlineData("DataType='t:BindingFixtureModel'")]
    public void ExplicitTemplateTypesDoNotForceCollectionBindingInference(string dataType)
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='x:String' ItemsSource='{CompiledBinding Rows, DataType=t:ItemSourceFixtureModel}'><ItemsControl.ItemTemplate><DataTemplate " + dataType + "><TextBlock Text='{CompiledBinding Name}'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            control.DataContext = new ItemSourceFixtureModel();
            AssertTemplate(control.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>())));
        }
    }

    [AvaloniaFact]
    public void NestedCollectionsEstablishIndependentItemScopes()
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:ItemSourceFixtureModel' ItemsSource='{CompiledBinding Groups}'><ItemsControl.ItemTemplate><DataTemplate><ItemsControl ItemsSource='{CompiledBinding Rows}'>" + Template + "</ItemsControl></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
        {
            var group = new ItemSourceFixtureModel();
            var nested = Assert.IsType<ItemsControl>(control.ItemTemplate!.Build(group));
            nested.DataContext = group;
            AssertTemplate(nested.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(nested.ItemsSource!.Cast<object>())));
        }
    }

    [AvaloniaFact]
    public void AncestorLookupSelectsTheNearestMatchingControl()
    {
        var xaml = "<t:ItemInferenceGrid " + Ns + " Items='{x:Static t:ItemSourceFixtureValues.Strings}'><t:ItemInferenceGrid.Nested><t:DerivedItemInferenceGrid x:DataType='t:ItemSourceFixtureModel' Items='{CompiledBinding Rows}'><t:ItemInferenceGrid.Columns><t:ItemInferenceColumn Binding='{CompiledBinding Name}'/></t:ItemInferenceGrid.Columns></t:DerivedItemInferenceGrid></t:ItemInferenceGrid.Nested></t:ItemInferenceGrid>";
        foreach (var control in CompileBoth<ItemInferenceGrid>(xaml)) AssertBinding(Assert.Single(control.Nested!.Columns).Binding!);
    }

    [AvaloniaFact]
    public void TypedCollectionProvidersAreConstructedAndInvokedOnce()
    {
        var xaml = "<ItemsControl " + Ns + " ItemsSource='{t:CountedItems}'>" + Template + "</ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml, () => { CountedItemsExtension.Constructions = 0; CountedItemsExtension.Calls = 0; }))
        {
            Assert.Equal(1, CountedItemsExtension.Constructions);
            Assert.Equal(1, CountedItemsExtension.Calls);
            AssertTemplate(control.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>())));
        }
    }

    [AvaloniaFact]
    public void ACollectionBindingSourceIsConstructedOnceWhenTheTemplateComesFirst()
    {
        var xaml = "<ItemsControl " + Ns + " x:DataType='t:CountedItemSourceModel'>" + Template + "<ItemsControl.ItemsSource><CompiledBindingExtension Path='Rows'><CompiledBindingExtension.Source><t:CountedItemSourceModel/></CompiledBindingExtension.Source></CompiledBindingExtension></ItemsControl.ItemsSource></ItemsControl>";
        foreach (var control in CompileBoth<ItemsControl>(xaml, () => CountedItemSourceModel.Constructions = 0))
        {
            Assert.Equal(1, CountedItemSourceModel.Constructions);
            AssertTemplate(control.ItemTemplate!, Assert.IsType<BindingFixtureModel>(Assert.Single(control.ItemsSource!.Cast<object>())));
        }
    }

    [AvaloniaTheory]
    [InlineData("<t:SelfItemControl", "ItemsSource='{CompiledBinding $self.Rows}'", "ItemTemplate", "</t:SelfItemControl>")]
    [InlineData("<ItemsControl", "ItemsSource='{x:Static t:ItemSourceFixtureValues.UntypedRows}'", "ItemTemplate", "</ItemsControl>")]
    [InlineData("<ItemsControl", "x:DataType='t:ItemSourceFixtureModel' ItemsSource='{CompiledBinding Rows}'", "DataTemplates", "</ItemsControl>")]
    public void InferenceRequiresCollectionMetadataAndAnAnnotatedTarget(string opening, string attributes, string property, string closing)
    {
        var xaml = opening + " " + Ns + " " + attributes + "><ItemsControl." + property + "><DataTemplate><TextBlock Text='{CompiledBinding Name}'/></DataTemplate></ItemsControl." + property + ">" + closing;
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Items.axaml", xaml) }).Result.Success);
    }

    [AvaloniaFact]
    public void AnUnknownDataContextStillAllowsAQualifiedCollectionSource()
    {
        var xaml = "<t:SelfItemControl " + Ns + " DataContext='{ReflectionBinding}' ItemsSource='{CompiledBinding $self.Rows}'>" + Template + "</t:SelfItemControl>";
        foreach (var control in CompileBoth<SelfItemControl>(xaml)) AssertTemplate(control.ItemTemplate!, Assert.Single(control.Rows));
    }

    [AvaloniaFact]
    public void APropertyCanProvideItsOwnCollectionBindingType()
    {
        var xaml = "<t:SelfItemBindingHost " + Ns + " x:DataType='t:ItemSourceFixtureModel' Items='{CompiledBinding Rows}'/>";
        foreach (var host in CompileBoth<SelfItemBindingHost>(xaml))
        {
            var model = new ItemSourceFixtureModel();
            var control = new ContentControl { DataContext = model };
            using var subscription = control.Bind(ContentControl.ContentProperty, host.Items!);
            Assert.Same(model.Rows, control.Content);
        }
    }

    private static void AssertTemplate(IDataTemplate template, BindingFixtureModel item)
    {
        var text = Assert.IsType<TextBlock>(template.Build(item));
        text.DataContext = item;
        Assert.Equal(item.Name, text.Text);
        item.Name = "changed item";
        Assert.Equal(item.Name, text.Text);
    }

    private static void AssertBinding(BindingBase binding)
    {
        Assert.IsType<CompiledBinding>(binding);
        var item = new BindingFixtureModel { Name = "assigned item" };
        var text = new TextBlock { DataContext = item };
        using var subscription = text.Bind(TextBlock.TextProperty, binding);
        Assert.Equal(item.Name, text.Text);
        item.Name = "changed binding";
        Assert.Equal(item.Name, text.Text);
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml, Action? beforeCompile = null)
    {
        beforeCompile?.Invoke();
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        beforeCompile?.Invoke();
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Items.axaml", xaml) }).Build("Items.axaml"));
    }
}

public class ItemSourceFixtureModel
{
    public ObservableCollection<BindingFixtureModel> Rows { get; } = new() { new() { Name = "collection item" } };
    public IEnumerable<BindingFixtureModel> InterfaceRows => Rows;
    public BindingFixtureModel[] ArrayRows => Rows.ToArray();
    public IEnumerable<ItemSourceFixtureModel> Groups => new[] { new ItemSourceFixtureModel() };
}

public sealed class ItemFixtureCollection : List<BindingFixtureModel> { }
public static class ItemSourceFixtureValues
{
    public static IEnumerable<BindingFixtureModel> Rows => new[] { new BindingFixtureModel { Name = "static item" } };
    public static IEnumerable<string> Strings => new[] { "outer string" };
    public static IEnumerable UntypedRows => Rows;
}

public class ItemInferenceGrid : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty = AvaloniaProperty.Register<ItemInferenceGrid, IEnumerable?>(nameof(Items));
    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public List<ItemInferenceColumn> Columns { get; } = new();
    public ItemInferenceGrid? Nested { get; set; }
}

public sealed class DerivedItemInferenceGrid : ItemInferenceGrid { }
public sealed class ItemInferenceColumn
{
    [AssignBinding, InheritDataTypeFromItems(nameof(ItemInferenceGrid.Items), AncestorType = typeof(ItemInferenceGrid))]
    public BindingBase? Binding { get; set; }
    [InheritDataTypeFromItems(nameof(ItemInferenceGrid.Items), AncestorType = typeof(ItemInferenceGrid))]
    public IDataTemplate? Template { get; set; }
}

public sealed class SelfItemControl : ItemsControl
{
    public IEnumerable<BindingFixtureModel> Rows => ItemSourceFixtureValues.Rows;
}

public sealed class CountedItemsExtension
{
    public static int Constructions { get; set; }
    public static int Calls { get; set; }
    public CountedItemsExtension() => Constructions++;
    public ItemFixtureCollection ProvideValue()
    {
        Calls++;
        return new() { new BindingFixtureModel { Name = "provided item" } };
    }
}

public sealed class CountedItemSourceModel : ItemSourceFixtureModel
{
    public static int Constructions { get; set; }
    public CountedItemSourceModel() => Constructions++;
}

public sealed class SelfItemBindingHost
{
    [AssignBinding, InheritDataTypeFromItems(nameof(Items))]
    public BindingBase? Items { get; set; }
}
