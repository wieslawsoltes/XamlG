using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class ProjectRenameTests
{
    private const string Namespaces = "xmlns='clr-namespace:Model' xmlns:m='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private const string Model = """
        using System;
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Model {
            public class Panel { [Content] public List<object> Children {get;} = new(); }
            public class Item { public string Name {get;set;} public string Text {get;set;} public object Target {get;set;} public Alignment Alignment {get;set;} public event EventHandler Click; }
            public enum Alignment { Left, Right }
            public static class Names { public const string Title = "Title"; }
            public class Data { }
            public class Box<T> { public object Target {get;set;} }
            public class FormatExtension { public string Value {get;set;} public string ProvideValue() => Value; }
        }
        """;
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Fact]
    public void Property_rename_updates_attribute_and_property_elements_across_documents()
    {
        var sources = Sources(("First.xaml", $"<Item {Namespaces} Text='Text'/>"), ("Second.xaml", $"<Item {Namespaces}><Item.Text>Text</Item.Text></Item>"),
            ("Use.cs", "class Use { string Read(Model.Item item) => item.Text + nameof(item.Text) + \"Text\"; }"));
        var result = Rename(sources, "Model.cs", Model.IndexOf("Text {", StringComparison.Ordinal), "Label");
        Assert.Contains(" Label='Text'", result["First.xaml"]);
        Assert.Contains("<Item.Label>Text</Item.Label>", result["Second.xaml"]);
        Assert.Contains("item.Label + nameof(item.Label) + \"Text\"", result["Use.cs"]);
    }

    [Fact]
    public void Xaml_type_selection_updates_opening_closing_and_owner_names()
    {
        var xaml = $"<m:Item {Namespaces}><m:Item.Target><Item/></m:Item.Target></m:Item>";
        var sources = Sources(("View.xaml", xaml)); var service = Create(sources);
        var target = service.Prepare("View.xaml", xaml.IndexOf("m:Item", StringComparison.Ordinal) + 2)!;
        Assert.Equal("Item", target.Name);
        var result = Rename(sources, "View.xaml", target.Span.Start, "Widget");
        Assert.Contains("<m:Widget ", result["View.xaml"]);
        Assert.Contains("<m:Widget.Target><Widget/></m:Widget.Target></m:Widget>", result["View.xaml"]);
        Assert.Contains("class Widget ", result["Model.cs"]);
    }

    [Fact]
    public void Codebehind_class_and_constructor_rename_regenerates_loader_and_named_fields()
    {
        const string code = "namespace Model { public partial class View : Panel { public View() { InitializeComponent(); } public string Read() => target.Text; } }";
        var sources = Sources(("View.xaml", $"<Panel {Namespaces} x:Class='Model.View'><Item x:Name='target' Text='ok'/></Panel>"), ("View.cs", code));
        var result = Rename(sources, "View.cs", code.IndexOf("View :", StringComparison.Ordinal), "Screen");
        Assert.Contains("x:Class='Model.Screen'", result["View.xaml"]);
        Assert.Contains("partial class Screen : Panel { public Screen()", result["View.cs"]);
        Assert.Contains("target.Text", result["View.cs"]);
    }

    [Fact]
    public void Namespace_rename_updates_namespace_uris_codebehind_and_qualified_source()
    {
        const string code = "namespace Model { public partial class View : Panel { public Model.Item New() => new Model.Item(); } }";
        var sources = Sources(("View.xaml", $"<Panel {Namespaces} x:Class='Model.View'><Item/></Panel>"), ("View.cs", code));
        var result = Rename(sources, "Model.cs", Model.IndexOf("namespace Model", StringComparison.Ordinal) + 10, "Presentation");
        Assert.DoesNotContain("clr-namespace:Model", result["View.xaml"]);
        Assert.Contains("x:Class='Presentation.View'", result["View.xaml"]);
        Assert.Contains("new Presentation.Item()", result["View.cs"]);
    }

    [Fact]
    public void Generated_field_selection_changes_owning_xaml_and_source_uses_only()
    {
        const string code = "namespace Model { public partial class View : Panel { public string Read() => target.Text + nameof(target) + \"target\"; } }";
        var sources = Sources(("View.xaml", $"<Panel {Namespaces} x:Class='Model.View'><Item x:Name='target'/><Item Target='{{x:Reference target}}'/></Panel>"), ("View.cs", code));
        var result = Rename(sources, "View.cs", code.IndexOf("target.Text", StringComparison.Ordinal), "selected");
        Assert.Contains("x:Name='selected'", result["View.xaml"]);
        Assert.Contains("{x:Reference selected}", result["View.xaml"]);
        Assert.Contains("selected.Text + nameof(selected) + \"target\"", result["View.cs"]);
    }

    [Theory]
    [InlineData("enum", "Left, Right", "Start", "Alignment='Le&#102;t' Text='Left'", "Alignment='Start' Text='Left'")]
    [InlineData("static", "Title =", "Heading", "Text='{x:Static m:Names.Ti&#116;le}'", "Text='{x:Static m:Names.Heading}'")]
    [InlineData("extension", "FormatExtension {", "DisplayExtension", "Text='{Format Value=ok}'", "Text='{Display Value=ok}'")]
    public void Resolved_value_references_rename_with_xml_entities_and_extension_suffixes(string kind, string marker, string name, string attributes, string expected)
    {
        Assert.NotEmpty(kind);
        var sources = Sources(("View.xaml", $"<Item {Namespaces} {attributes}/>"));
        var result = Rename(sources, "Model.cs", Model.IndexOf(marker, StringComparison.Ordinal), name);
        Assert.Contains(expected, result["View.xaml"]);
    }

    [Fact]
    public void Type_arguments_and_type_markup_keep_raw_xml_spans()
    {
        var sources = Sources(("View.xaml", $"<Box {Namespaces} x:TypeArguments='m:D&#97;ta' Target='{{x:Type m:D&#97;ta}}'/>"));
        var result = Rename(sources, "Model.cs", Model.IndexOf("Data {", StringComparison.Ordinal), "Payload");
        Assert.Contains("x:TypeArguments='m:Payload'", result["View.xaml"]);
        Assert.Contains("Target='{x:Type m:Payload}'", result["View.xaml"]);
    }

    [Fact]
    public void Markup_property_rename_preserves_encoded_value_and_uses_the_complete_encoded_name()
    {
        var sources = Sources(("View.xaml", $"<Item {Namespaces} Text='{{Format V&#97;lue = \"he&#108;lo\"}}'/>"));
        var result = Rename(sources, "Model.cs", Model.IndexOf("Value {", StringComparison.Ordinal), "Caption");
        Assert.Contains("Text='{Format Caption = \"he&#108;lo\"}'", result["View.xaml"]);
        Assert.Contains("ProvideValue() => Caption", result["Model.cs"]);
    }

    [Fact]
    public void Handler_rename_follows_bound_event_reference()
    {
        const string code = "namespace Model { public partial class View : Panel { public void OnClick(object sender, System.EventArgs args) { } } }";
        var sources = Sources(("View.xaml", $"<Panel {Namespaces} x:Class='Model.View'><Item Click='OnClick' Text='OnClick'/></Panel>"), ("View.cs", code));
        var result = Rename(sources, "View.cs", code.IndexOf("OnClick", StringComparison.Ordinal), "HandleClick");
        Assert.Contains("Click='HandleClick' Text='OnClick'", result["View.xaml"]);
    }

    [Fact]
    public void Capturing_a_generated_field_is_rejected_without_changing_the_snapshot()
    {
        const string code = "namespace Model { public partial class View : Panel { public string Read() { var selected = new Item(); return target.Text; } } }";
        var sources = Sources(("View.xaml", $"<Panel {Namespaces} x:Class='Model.View'><Item x:Name='target'/></Panel>"), ("View.cs", code));
        Assert.Throws<InvalidOperationException>(() => Create(sources).Rename("View.cs", code.IndexOf("target.Text", StringComparison.Ordinal), "selected"));
        Assert.Equal(code, sources["View.cs"]); Assert.Contains("x:Name='target'", sources["View.xaml"]);
    }

    private static Dictionary<string, string> Sources(params (string Path, string Text)[] files) =>
        files.Append((Path: "Model.cs", Text: Model)).ToDictionary(file => file.Path, file => file.Text, StringComparer.Ordinal);

    private static XamlProjectRenameService Create(IReadOnlyDictionary<string, string> sources)
    {
        var application = CSharpCompilation.Create("ProjectRename", sources.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal))
            .Select(file => CSharpSyntaxTree.ParseText(file.Value, new CSharpParseOptions(LanguageVersion.Preview), file.Key)), References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var inputs = sources.Where(file => file.Key.EndsWith(".xaml", StringComparison.Ordinal)).Select(file =>
            new XamlProjectDocument(XamlSyntaxTree.Parse(file.Value, file.Key, version: 7), file.Key)).ToArray();
        var compiler = new XamlCompilationSession(application, projectDocuments: inputs);
        var project = compiler.CompileProject(inputs.Select(input => input.Syntax));
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(document => document.Output.Diagnostics)));
        var compilation = XamlCSharpCompilation.AddGeneratedSources(application, project);
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return new(compiler, project, compilation, application.SyntaxTrees.Select(tree => tree.FilePath));
    }
    private static Dictionary<string, string> Rename(Dictionary<string, string> sources, string path, int offset, string name)
    {
        var plan = Create(sources).Rename(path, offset, name);
        Assert.NotEmpty(plan.Documents);
        var result = new Dictionary<string, string>(sources, StringComparer.Ordinal);
        foreach (var edit in plan.Documents)
        {
            Assert.Equal(sources[edit.Path], edit.OriginalText); Assert.DoesNotContain(".g.cs", edit.Path);
            result[edit.Path] = Microsoft.CodeAnalysis.Text.SourceText.From(edit.OriginalText).WithChanges(edit.Changes.Select(change =>
                new Microsoft.CodeAnalysis.Text.TextChange(new(change.Span.Start, change.Span.Length), change.NewText))).ToString();
        }
        Create(result); // Compile the actual returned source edits with regenerated XAML sources.
        return result;
    }
}
