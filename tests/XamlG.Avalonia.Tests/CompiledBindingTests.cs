using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class CompiledBindingTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    private const string Typed = " x:DataType='vm:BindingFixtureModel'";

    [AvaloniaFact]
    public void PropertyChainsObserveChangesAndReplaceSubscriptions()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + " Text='{CompiledBinding Child.Name}'/>");
        var first = new BindingFixtureModel { Name = "one" };
        var model = new BindingFixtureModel { Child = first }; root.DataContext = model;
        Assert.Equal("one", root.Text);
        first.Name = "updated"; Assert.Equal("updated", root.Text);
        model.Child = new() { Name = "replacement" }; Assert.Equal("replacement", root.Text);
        first.Name = "detached"; Assert.Equal("replacement", root.Text);
    }

    [AvaloniaFact]
    public void TwoWayBindingWritesThroughGeneratedSetter()
    {
        var root = (TextBox)AvaloniaCompilation.Build("<TextBox " + Ns + Typed + " Text='{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}'/>");
        var model = new BindingFixtureModel(); root.DataContext = model;
        root.Text = "edited"; Dispatcher.UIThread.RunJobs();
        Assert.Equal("edited", model.Name);
        model.Name = "external"; Assert.Equal("external", root.Text);
    }

    [AvaloniaTheory]
    [InlineData("Items[1]", "second")]
    [InlineData("Lookup[answer]", "42")]
    [InlineData("Numbers[0]", "7")]
    [InlineData("Message^", "task result")]
    public void CompilesIndexersArraysAndTaskStreams(string path, string expected)
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + " Text='{CompiledBinding " + path + "}'/>");
        root.DataContext = new BindingFixtureModel(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(expected, root.Text);
    }

    [AvaloniaFact]
    public void ObservableIndexerRespondsToCollectionChanges()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + " Text='{CompiledBinding Items[0]}'/>");
        var model = new BindingFixtureModel(); root.DataContext = model;
        model.Items[0] = "replaced"; Assert.Equal("replaced", root.Text);
        model.Items.Insert(0, "inserted"); Assert.Equal("inserted", root.Text);
    }

    [AvaloniaFact]
    public void NamedForwardAndSelfSourcesUseGeneratedNamescope()
    {
        var root = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Ns + "><TextBlock Text='{CompiledBinding #input.Text}'/><TextBox x:Name='input' Text='named'/><TextBlock Tag='self' Text='{CompiledBinding $self.Tag}'/></StackPanel>");
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("named", ((TextBlock)root.Children[0]).Text);
            Assert.Equal("self", ((TextBlock)root.Children[2]).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompileBindingsDirectiveCanOptOutOfStaticChecking()
    {
        var root = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + " x:CompileBindings='False' Text='{Binding Name}'/>");
        root.DataContext = new BindingFixtureModel { Name = "reflection opt-out" };
        Assert.Equal("reflection opt-out", root.Text);
    }

    [Fact]
    public void MissingMembersAndReadOnlyWritePathsHaveCompileDiagnostics()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(BindingFixtureModel).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("BindingDiagnostic", references: references, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var test in new[] { ("Missing", "XG3205"), ("ReadOnly, Mode=TwoWay", "XG3212") })
        {
            var source = "<TextBlock " + Ns + Typed + " Text='{CompiledBinding " + test.Item1 + "}'/>";
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(source), compilation, AvaloniaFrameworkProfile.Create());
            Assert.Contains(bound.Diagnostics, d => d.Code == test.Item2);
        }
    }

    [Theory]
    [InlineData("A..B")]
    [InlineData("A[")]
    [InlineData("A[]")]
    [InlineData("$unknown.Name")]
    [InlineData("A.")]
    [InlineData("((vm:Type)Value")]
    public void InvalidPathsAreRecoveredAsDiagnostics(string path)
    {
        var diagnostics = new List<XamlDiagnostic>();
        Assert.Null(BindingPathParser.Parse(path, new(0, path.Length), diagnostics.Add));
        Assert.NotEmpty(diagnostics);
    }
}
