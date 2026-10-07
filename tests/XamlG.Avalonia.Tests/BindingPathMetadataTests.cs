using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class BindingPathMetadataTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("#source.ChildControl.DataContext.Name")]
    [InlineData("#source[0].DataContext.Name")]
    [InlineData("#source^.DataContext.Name")]
    [InlineData("#source.(Control).DataContext.Name")]
    [InlineData("#source.(t:PathMetadataControl.Related).DataContext.Name")]
    [InlineData("ChildControl.DataContext.Name, ElementName=source")]
    [InlineData("[0].DataContext.Name, ElementName=source")]
    public void IntermediatePathNodesDoNotRetainNamedDataContextMetadata(string path)
    {
        Reject("<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><t:PathMetadataControl Name='source'/><TextBlock Text='{CompiledBinding " + path + "}'/></StackPanel>");
    }

    [AvaloniaTheory]
    [InlineData("$self.DataContext.Name")]
    [InlineData("DataContext.Name, RelativeSource={RelativeSource Self}")]
    public void SelfSourcesDoNotSupplyInferredDataContextMetadata(string path)
    {
        Reject("<TextBlock " + Ns + " x:DataType='t:BindingFixtureModel' Text='{CompiledBinding " + path + "}'/>");
    }

    [AvaloniaTheory]
    [InlineData("$parent[t:PathMetadataControl].ChildControl.DataContext.Name")]
    [InlineData("DataContext.Name, RelativeSource={RelativeSource FindAncestor, AncestorType=t:PathMetadataControl, Tree=Visual}")]
    public void AncestorMetadataRequiresAnImmediateLogicalSource(string path)
    {
        Reject("<t:PathMetadataControl " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding " + path + "}'/></t:PathMetadataControl>");
    }

    [AvaloniaTheory]
    [InlineData("$parent[t:PathMetadataControl].DataContext.Name")]
    public void ImmediateLogicalAncestorsRetainTheirDataContextType(string path)
    {
        var xaml = "<t:PathMetadataControl " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding " + path + "}'/></t:PathMetadataControl>";
        foreach (var root in CompileBoth<PathMetadataControl>(xaml))
        {
            var model = new BindingFixtureModel { Name = "ancestor" };
            root.DataContext = model;
            var text = Assert.IsType<TextBlock>(root.Content);
            Assert.Equal("ancestor", text.Text);
            model.Name = "changed";
            Assert.Equal("changed", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("$self.DataContext.(t:BindingFixtureModel).Name")]
    [InlineData("((t:BindingFixtureModel)DataContext).Name, RelativeSource={RelativeSource Self}")]
    public void ExplicitCastsSupplySelfDataContextTypes(string path)
    {
        foreach (var text in CompileBoth<TextBlock>("<TextBlock " + Ns + " Text='{CompiledBinding " + path + "}'/>"))
        {
            text.DataContext = new BindingFixtureModel { Name = "explicit" };
            Assert.Equal("explicit", text.Text);
        }
    }

    [AvaloniaFact]
    public void AClrPropertyNamedDataContextUsesItsOwnType()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><StackPanel.Resources><t:PathMetadataObject x:Key='item' Name='source'/></StackPanel.Resources><TextBlock Text='{CompiledBinding #source.DataContext.Length}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
            Assert.Equal("6", Assert.IsType<TextBlock>(Assert.Single(root.Children)).Text);
    }

    [AvaloniaFact]
    public void ACustomDataContextRegistrationUsesItsOwnType()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><t:PathCustomDataContextControl Name='source'/><TextBlock Text='{CompiledBinding #source.DataContext.Length}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<PathCustomDataContextControl>(root.Children[0]);
            var text = Assert.IsType<TextBlock>(root.Children[1]);
            Assert.Equal("6", text.Text);
            source.SetValue(PathCustomDataContextControl.DataContextProperty, "changed");
            Assert.Equal("7", text.Text);
        }
    }

    [AvaloniaFact]
    public void AHiddenClrDataContextDoesNotReplaceTheStyledElementRegistration()
    {
        var xaml = "<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><t:PathHiddenDataContextControl Name='source'/><TextBlock Text='{CompiledBinding #source.DataContext.Name}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var model = new BindingFixtureModel { Name = "registered context" };
            root.DataContext = model;
            var text = Assert.IsType<TextBlock>(root.Children[1]);
            Assert.Equal("registered context", text.Text);
            model.Name = "updated context";
            Assert.Equal("updated context", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("#source.Wrapped.Length", false)]
    [InlineData("#source.(t:PathRegistrationBase.Wrapped).Length", true)]
    public void PathRegistrationsUseTheirSelectedOwner(string path, bool qualified)
    {
        var xaml = "<StackPanel " + Ns + "><t:PathRegistrationDerived Name='source'/><TextBlock Text='{CompiledBinding " + path + "}'/></StackPanel>";
        if (!qualified) { Reject(xaml); return; }
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<PathRegistrationDerived>(root.Children[0]);
            var text = Assert.IsType<TextBlock>(root.Children[1]);
            Assert.Equal("4", text.Text);
            source.SetValue(PathRegistrationBase.WrappedProperty, "changed");
            Assert.Equal("7", text.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("#source.Registered.Length")]
    [InlineData("#source.Wrapped.Length")]
    public void RegisteredPropertiesSupplyTypesWithoutCompatibleClrWrappers(string path)
    {
        var xaml = "<StackPanel " + Ns + "><t:PathMetadataControl Name='source'/><TextBlock Text='{CompiledBinding " + path + "}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<PathMetadataControl>(root.Children[0]);
            var text = Assert.IsType<TextBlock>(root.Children[1]);
            Assert.Equal("7", text.Text);
            source.SetValue(PathMetadataControl.RegisteredProperty, "updated value");
            source.SetValue(PathMetadataControl.WrappedProperty, "updated value");
            Assert.Equal("13", text.Text);
        }
    }

    [AvaloniaFact]
    public void AReadOnlyClrWrapperDoesNotPreventWritingARegisteredBindingSource()
    {
        var xaml = "<StackPanel " + Ns + "><t:PathMetadataControl Name='source'/><TextBox Text='{CompiledBinding #source.Wrapped, Mode=TwoWay}'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<PathMetadataControl>(root.Children[0]);
            var text = Assert.IsType<TextBox>(root.Children[1]);
            Assert.Equal("initial", text.Text);
            text.Text = "written";
            Assert.Equal("written", source.GetValue(PathMetadataControl.WrappedProperty));
        }
    }

    [AvaloniaFact]
    public void AttachedDataContextSyntaxDoesNotInferItsResultType()
    {
        Reject("<StackPanel " + Ns + " x:DataType='t:BindingFixtureModel'><Control Name='source'/><TextBlock Text='{CompiledBinding #source.(Control.DataContext).Name}'/></StackPanel>");
    }

    [Fact]
    public void RegisteredBindingNavigationRetainsClrWrappersAndUnwrappedFields()
    {
        var xaml = "<StackPanel " + Ns + "><t:PathMetadataControl Name='source'/><TextBlock Text='{CompiledBinding #source.Registered}'/><TextBlock Text='{CompiledBinding #source.Wrapped}'/></StackPanel>";
        var fixture = new ResourceProjectFixture(new[] { ("Metadata.axaml", xaml) });
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, cancellationToken: TestContext.Current.CancellationToken), fixture.Compilation, AvaloniaFrameworkProfile.Create(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(document.Success);
        var references = document.Symbols.Where(symbol => symbol.Role == "binding-member").ToArray();
        Assert.Equal("RegisteredProperty", Assert.IsAssignableFrom<IFieldSymbol>(Assert.Single(references, symbol => xaml.Substring(symbol.Span.Start, symbol.Span.Length) == "Registered").Symbol).Name);
        Assert.Equal("Wrapped", Assert.IsAssignableFrom<IPropertySymbol>(Assert.Single(references, symbol => xaml.Substring(symbol.Span.Start, symbol.Span.Length) == "Wrapped").Symbol).Name);
    }

    [AvaloniaFact]
    public void LogicalRelativeSourceRetainsNativeAncestorMetadataSupport()
    {
        var xaml = "<t:PathMetadataControl " + Ns + " x:DataType='t:BindingFixtureModel'><TextBlock Text='{CompiledBinding DataContext.Name, RelativeSource={RelativeSource FindAncestor, AncestorType=t:PathMetadataControl, Tree=Logical}}'/></t:PathMetadataControl>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var root = Assert.IsType<PathMetadataControl>(new ResourceProjectFixture(new[] { ("Metadata.axaml", xaml) }).Build("Metadata.axaml"));
        var model = new BindingFixtureModel { Name = "logical ancestor" };
        root.DataContext = model;
        var text = Assert.IsType<TextBlock>(root.Content);
        Assert.Equal("logical ancestor", text.Text);
        model.Name = "updated ancestor";
        Assert.Equal("updated ancestor", text.Text);
    }

    private static void Reject(string xaml)
    {
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Metadata.axaml", xaml) }).Result.Success);
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Metadata.axaml", xaml) }).Build("Metadata.axaml"));
    }
}

public sealed class PathMetadataControl : ContentControl, IObservable<Control>
{
    public static readonly StyledProperty<Control?> RelatedProperty = AvaloniaProperty.Register<PathMetadataControl, Control?>("Related");
    public static readonly StyledProperty<string> RegisteredProperty = AvaloniaProperty.Register<PathMetadataControl, string>("Registered", "initial");
    public static readonly StyledProperty<string> WrappedProperty = AvaloniaProperty.Register<PathMetadataControl, string>(nameof(Wrapped), "initial");
    public object Wrapped => "CLR wrapper";
    public Control ChildControl { get; } = new();
    public Control this[int index] => ChildControl;
    public IDisposable Subscribe(IObserver<Control> observer)
    {
        observer.OnNext(ChildControl);
        return new Subscription();
    }
    private sealed class Subscription : IDisposable { public void Dispose() { } }
}

public sealed class PathMetadataObject : INamed
{
    public string? Name { get; set; }
    public string DataContext => "custom";
}

public sealed class PathCustomDataContextControl : Control
{
    public new static readonly StyledProperty<string> DataContextProperty = AvaloniaProperty.Register<PathCustomDataContextControl, string>(nameof(DataContext), "custom");
    public new string DataContext { get => GetValue(DataContextProperty); set => SetValue(DataContextProperty, value); }
}

public sealed class PathHiddenDataContextControl : Control
{
    public new string DataContext => "CLR context";
}

public class PathRegistrationBase : Control
{
    public static readonly StyledProperty<string> WrappedProperty = AvaloniaProperty.Register<PathRegistrationBase, string>(nameof(Wrapped), "base");
    public string Wrapped { get => GetValue(WrappedProperty); set => SetValue(WrappedProperty, value); }
}

public sealed class PathRegistrationDerived : PathRegistrationBase
{
    public new static readonly StyledProperty<int> WrappedProperty = AvaloniaProperty.Register<PathRegistrationDerived, int>(nameof(Wrapped), 42);
}
