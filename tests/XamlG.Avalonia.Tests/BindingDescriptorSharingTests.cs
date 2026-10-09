using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class BindingDescriptorSharingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescriptorHelpersSurviveFailedOrRemovedOriginalDocuments(bool failedOwner)
    {
        var fixture = new ResourceProjectFixture(Array.Empty<(string, string)>());
        var compiler = new XamlProjectCompiler();
        var profile = AvaloniaFrameworkProfile.Create();
        static XamlProjectDocument Document(string path, string member) => new(XamlSyntaxTree.Parse(
            "<TextBlock " + ResourceProjectFixture.Namespace +
            " xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' x:DataType='vm:BindingFixtureModel' Text='{CompiledBinding " + member + "}'/>", path), path);
        var first = Document("First.axaml", "Name");
        var second = Document("Second.axaml", "Name");
        var initial = compiler.Compile(new[] { first, second }, fixture.Compilation, profile);
        Assert.True(initial.Success);
        Assert.Single(initial.Documents.SelectMany(document => CSharpSyntaxTree.ParseText(document.Output.Source).GetRoot()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>()), node => node.Type.ToString() == "global::Avalonia.Data.Core.ClrPropertyInfo");
        var changed = compiler.Compile(failedOwner ? new[] { Document("First.axaml", "Missing"), second } : new[] { second }, fixture.Compilation, profile);
        Assert.Equal(!failedOwner, changed.Success);
        var survivor = changed.Documents.Single(document => document.Input.LogicalPath == second.LogicalPath).Output;
        using var bytes = new MemoryStream();
        var emitted = fixture.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(survivor.Source,
            new CSharpParseOptions(LanguageVersion.Preview))).Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = ResourceProjectFixture.Load(bytes.ToArray());
        var root = Assert.IsType<TextBlock>(assembly.GetType(survivor.FactoryMetadataName)!
            .GetMethod(survivor.BuildMethodName!)!.Invoke(null, new object?[] { null }));
        root.DataContext = new BindingFixtureModel { Name = "survived" };
        Assert.Equal("survived", root.Text);
        var restored = compiler.Compile(new[] { first, second }, fixture.Compilation, profile);
        Assert.Equal(initial.Documents.Select(document => document.Output.Source), restored.Documents.Select(document => document.Output.Source));
    }

    [AvaloniaFact]
    public void SharedDescriptorsKeepIndependentTargetsSubscriptionsAndIndexerArguments()
    {
        const string xaml = """
            <StackPanel xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                        xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' x:DataType='vm:BindingFixtureModel'>
              <TextBox Text='{CompiledBinding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}'/>
              <TextBox Text='{CompiledBinding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}'/>
              <TextBlock Text='{CompiledBinding Items[0]}'/>
              <TextBlock Text='{CompiledBinding Items[1]}'/>
              <TextBlock Text='{CompiledBinding Items[0]}'/>
            </StackPanel>
            """;
        var fixture = new ResourceProjectFixture(new[] { ("Shared.axaml", xaml) });
        var first = Assert.IsType<StackPanel>(fixture.Build("Shared.axaml"));
        var firstModel = new BindingFixtureModel { Name = "first" };
        first.DataContext = firstModel;
        Assert.IsType<TextBox>(first.Children[0]).Text = "edited";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("edited", firstModel.Name);
        Assert.Equal("edited", Assert.IsType<TextBox>(first.Children[1]).Text);
        firstModel.Items[0] = "replaced";
        Assert.Equal("replaced", Assert.IsType<TextBlock>(first.Children[2]).Text);
        Assert.Equal("second", Assert.IsType<TextBlock>(first.Children[3]).Text);
        Assert.Equal("replaced", Assert.IsType<TextBlock>(first.Children[4]).Text);
        var replacement = new BindingFixtureModel { Name = "replacement" };
        first.DataContext = replacement;
        firstModel.Name = "detached";
        Assert.Equal("replacement", Assert.IsType<TextBox>(first.Children[0]).Text);
        // One immutable descriptor for Name, Items and each distinct index.
        var constructors = CSharpSyntaxTree.ParseText(fixture.Result.Documents.Single().Output.Source).GetRoot()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Count(node => node.Type.ToString() == "global::Avalonia.Data.Core.ClrPropertyInfo");
        Assert.Equal(4, constructors);
        Assert.Equal(3, CSharpSyntaxTree.ParseText(fixture.Result.Documents.Single().Output.Source).GetRoot()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Count(node => node.Type.ToString() == "global::Avalonia.Data.CompiledBindingPathBuilder"));
    }

    [AvaloniaFact]
    public void SharedSelfPathsAndLocalNamedPathsKeepTemplateInstancesIndependent()
    {
        var xaml = ResourceProjectFixture.Dictionary("""
            <DataTemplate x:Key='template'>
              <StackPanel>
                <TextBox x:Name='input' Text='initial'/>
                <TextBlock Text='{CompiledBinding #input.Text}'/>
                <TextBlock Text='{CompiledBinding $self.Tag}' Tag='self'/>
              </StackPanel>
            </DataTemplate>
            """);
        var fixture = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) });
        var dictionary = Assert.IsType<ResourceDictionary>(fixture.Build("Template.axaml"));
        var template = Assert.IsAssignableFrom<IDataTemplate>(dictionary["template"]);
        var first = Assert.IsType<StackPanel>(template.Build(null));
        var second = Assert.IsType<StackPanel>(template.Build(null));
        var container = new StackPanel();
        container.Children.Add(first); container.Children.Add(second);
        var window = new Window { Content = container };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.IsType<TextBox>(first.Children[0]).Text = "changed first";
            Assert.Equal("changed first", Assert.IsType<TextBlock>(first.Children[1]).Text);
            Assert.Equal("initial", Assert.IsType<TextBlock>(second.Children[1]).Text);
            Assert.Equal("self", Assert.IsType<TextBlock>(first.Children[2]).Text);
            Assert.Equal("self", Assert.IsType<TextBlock>(second.Children[2]).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosedGenericMembersAndHiddenMembersHaveDistinctDescriptors()
    {
        const string model = """
            namespace DescriptorModels;
            public class Slot<T> { public T Value { get; set; } }
            public class Base { public string Name => "base"; }
            public class Derived : Base { public new string Name => "derived"; }
            public class Model
            {
                public Slot<string> Text { get; } = new() { Value = "text" };
                public Slot<int> Number { get; } = new() { Value = 42 };
                public Base Base { get; } = new Derived();
                public Derived Derived { get; } = new();
            }
            """;
        const string xaml = """
            <StackPanel xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                        xmlns:vm='clr-namespace:DescriptorModels' x:DataType='vm:Model'>
              <StackPanel.DataContext><vm:Model/></StackPanel.DataContext>
              <TextBlock Text='{CompiledBinding Text.Value}'/>
              <TextBlock Text='{CompiledBinding Number.Value}'/>
              <TextBlock Text='{CompiledBinding Base.Name}'/>
              <TextBlock Text='{CompiledBinding Derived.Name}'/>
            </StackPanel>
            """;
        var fixture = new ResourceProjectFixture(new[] { ("Distinct.axaml", xaml) }, sourceCode: model);
        var root = Assert.IsType<StackPanel>(fixture.Build("Distinct.axaml"));
        Assert.Equal(new[] { "text", "42", "base", "derived" }, root.Children.Select(child => Assert.IsType<TextBlock>(child).Text));
    }
}
