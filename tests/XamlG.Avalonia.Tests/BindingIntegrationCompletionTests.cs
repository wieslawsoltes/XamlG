using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using XamlG.Compiler;
using XamlG.Frameworks;
using XamlG.Frameworks.Avalonia;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class BindingIntegrationCompletionTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    private const string Typed = " x:DataType='vm:BindingFixtureModel'";

    [AvaloniaTheory]
    [InlineData("<Binding Path='Name'/>")]
    [InlineData("<CompiledBinding Path='Name'/>")]
    [InlineData("<Binding><Binding.Path>Name</Binding.Path></Binding>")]
    [InlineData("<CompiledBinding><CompiledBinding.Path>Name</CompiledBinding.Path></CompiledBinding>")]
    public void ObjectElementFormsUseTheSameCheckedPath(string binding)
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + "><TextBlock.Text>" + binding + "</TextBlock.Text></TextBlock>");
        var model = new BindingFixtureModel { Name = "object form" };
        root.DataContext = model;
        Assert.Equal("object form", root.Text);
        model.Name = "updated";
        Assert.Equal("updated", root.Text);
    }

    [AvaloniaFact]
    public void ObjectElementBindingRetainsNestedSourceAndOptions()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + "><TextBlock.Text><CompiledBinding Path='Name'><CompiledBinding.Source><vm:BindingFixtureModel Name='explicit'/></CompiledBinding.Source><CompiledBinding.StringFormat>Value: {0}</CompiledBinding.StringFormat></CompiledBinding></TextBlock.Text></TextBlock>");
        Assert.Equal("Value: explicit", root.Text);
    }

    [AvaloniaTheory]
    [InlineData("{CompiledBinding Tag, RelativeSource={RelativeSource Self}}")]
    [InlineData("{Binding Tag, RelativeSource={RelativeSource Mode=Self}}")]
    public void RelativeSelfUsesTheControlInsteadOfItsDataContext(string binding)
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + " Tag='self value' Text='" + binding + "'/>");
        Assert.Equal("self value", root.Text);
    }

    [AvaloniaFact]
    public void RelativeSourceObjectFormResolvesSelf()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + " Tag='nested source'><TextBlock.Text><CompiledBinding Path='Tag'><CompiledBinding.RelativeSource><RelativeSource Mode='Self'/></CompiledBinding.RelativeSource></CompiledBinding></TextBlock.Text></TextBlock>");
        Assert.Equal("nested source", root.Text);
    }

    [AvaloniaTheory]
    [InlineData("Visual")]
    [InlineData("Logical")]
    public void RelativeAncestorUsesItsDeclaredTree(string tree)
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Ns + " Tag='ancestor'><TextBlock Text='{CompiledBinding Tag, RelativeSource={RelativeSource FindAncestor, AncestorType=StackPanel, Tree=" + tree + "}}'/></StackPanel>");
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("ancestor", ((TextBlock)root.Children[0]).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(" TargetType='{x:Type Button}'")]
    [InlineData("")]
    public void TemplatedParentInferenceUsesTheOwningControl(string templateTarget)
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Ns + " Content='template value'><Button.Template><ControlTemplate" + templateTarget + "><TextBlock Text='{CompiledBinding Content, RelativeSource={RelativeSource TemplatedParent}}'/></ControlTemplate></Button.Template></Button>");
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var text = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<TextBlock>()
                .First(block => block.Text == "template value");
            root.Content = "changed template value";
            Assert.Equal("changed template value", text.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void StyleBindingSelfRefersToTheStyledControlNotTheSetter()
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Ns + "><StackPanel.Styles><Style Selector='Button'><Setter Property='Content' Value='{CompiledBinding $self.Tag}'/></Style></StackPanel.Styles><Button Tag='styled source'/></StackPanel>");
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("styled source", ((Button)root.Children[0]).Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CommandTargetTypeSurvivesObjectValuedBindingLowering()
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Ns + Typed + " Command='{CompiledBinding Execute}'/>");
        var model = new BindingFixtureModel();
        root.DataContext = model;
        Assert.NotNull(root.Command);
        Assert.True(root.Command.CanExecute(null));
        root.Command.Execute(null);
        Assert.Equal(1, model.InvocationCount);
        model.Enabled = false;
        Assert.False(root.Command.CanExecute(null));
    }

    [AvaloniaFact]
    public void RuntimeSessionOwnsGeneratedBindingSubscriptions()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + " Text='{CompiledBinding Name}'/>");
        var model = new BindingFixtureModel { Name = "before disposal" };
        root.DataContext = model;
        Assert.Equal("before disposal", root.Text);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
        var detachedText = root.Text;
        model.Name = "must not update the retired graph";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(detachedText, root.Text);
        Assert.False(XamlRuntimeSession.TryGet(root, out _));
    }

    [Fact]
    public void ConflictingSourcesAreCompileErrorsRatherThanFallbacks()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ConflictingBinding", references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var syntax = XamlSyntaxTree.Parse("<TextBlock " + Ns + " Text='{CompiledBinding Tag, Source={x:Null}, RelativeSource={RelativeSource Self}}'/>", cancellationToken: TestContext.Current.CancellationToken);
        var result = new XamlCompiler().Bind(syntax, compilation, AvaloniaFrameworkProfile.Create(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "XG3210");
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "false", false)]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", false)]
    public void BuildDefaultsHonorExplicitXamlGThenAvaloniaOptions(string? xamlg, string? avalonia, bool expected)
    {
        var values = new Dictionary<string, string>();
        if (xamlg != null) values["build_property.XamlGCompileBindingsByDefault"] = xamlg;
        if (avalonia != null) values["build_property.AvaloniaUseCompiledBindingsByDefault"] = avalonia;
        Assert.Equal(expected, AvaloniaBuildOptions.ReadCompileBindingsByDefault(new Options(values)));
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
