using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using XamlG.Tooling;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ProjectRenameTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:m='clr-namespace:Model'";
    private const string Code = """
        using System;
        using Avalonia;
        using Avalonia.Controls;
        using Avalonia.Interactivity;
        namespace Model {
            public class Widget : Control {
                public static readonly StyledProperty<string> CaptionProperty = AvaloniaProperty.Register<Widget, string>("Caption");
                public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
                public static readonly RoutedEvent<RoutedEventArgs> FiredEvent = RoutedEvent.Register<Widget, RoutedEventArgs>("Fired", RoutingStrategies.Bubble);
                public event EventHandler<RoutedEventArgs> Fired { add => AddHandler(FiredEvent, value); remove => RemoveHandler(FiredEvent, value); }
            }
            public class Hints : AvaloniaObject {
                public static readonly AttachedProperty<string> NoteProperty = AvaloniaProperty.RegisterAttached<Hints, Control, string>("Note");
                public static string GetNote(Control target) => target.GetValue(NoteProperty);
                public static void SetNote(Control target, string value) => target.SetValue(NoteProperty, value);
            }
            public class ViewModel { public string Text { get; set; } = "bound"; }
            public partial class View : StackPanel {
                public int Raised;
                public void OnFired(object sender, RoutedEventArgs args) => Raised++;
            }
        }
        """;
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [AvaloniaTheory]
    [InlineData("xaml")]
    [InlineData("field")]
    [InlineData("wrapper")]
    public void Registered_property_rename_updates_field_wrapper_literal_and_live_value(string selection)
    {
        var xaml = $"<m:Widget {Ns} Caption='hello'/>";
        var path = selection == "xaml" ? "View.axaml" : "Code.cs";
        var offset = selection == "xaml" ? xaml.IndexOf("Caption=", StringComparison.Ordinal) : Code.IndexOf(selection == "field" ? "CaptionProperty =" : "Caption {", StringComparison.Ordinal);
        var result = Rename(xaml, path, offset, selection == "field" ? "TitleProperty" : "Title");
        Assert.Contains(" Title='hello'", result.Xaml);
        Assert.Contains("TitleProperty = AvaloniaProperty.Register<Widget, string>(\"Title\")", result.Code);
        Assert.Contains("string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }", result.Code);
        Assert.Equal("hello", result.Root.GetType().GetProperty("Title")!.GetValue(result.Root));
        var property = Assert.IsAssignableFrom<AvaloniaProperty>(result.Root.GetType().GetField("TitleProperty")!.GetValue(null));
        Assert.Equal("Title", property.Name);
    }

    [AvaloniaFact]
    public void Attached_property_selection_updates_both_accessors_and_registration()
    {
        var xaml = $"<TextBlock {Ns} m:Hints.Note='attached'/>";
        var result = Rename(xaml, "View.axaml", xaml.IndexOf("Hints.Note", StringComparison.Ordinal) + "Hints.".Length, "Hint");
        Assert.Contains("m:Hints.Hint='attached'", result.Xaml);
        Assert.Contains("HintProperty = AvaloniaProperty.RegisterAttached<Hints, Control, string>(\"Hint\")", result.Code);
        var owner = result.Assembly.GetType("Model.Hints")!;
        Assert.Null(owner.GetMethod("GetNote")); Assert.NotNull(owner.GetMethod("SetHint"));
        Assert.Equal("attached", owner.GetMethod("GetHint")!.Invoke(null, [result.Root]));
    }

    [AvaloniaFact]
    public void Routed_event_rename_preserves_codebehind_handler_and_runtime_subscription()
    {
        var xaml = $"<StackPanel {Ns} x:Class='Model.View'><m:Widget Fired='OnFired'/></StackPanel>";
        var result = Rename(xaml, "Code.cs", Code.IndexOf("FiredEvent =", StringComparison.Ordinal), "InvokedEvent");
        Assert.Contains("Invoked='OnFired'", result.Xaml);
        Assert.Contains("RoutedEvent.Register<Widget, RoutedEventArgs>(\"Invoked\"", result.Code);
        var root = Assert.IsAssignableFrom<StackPanel>(result.Root); var child = Assert.Single(root.Children);
        var routedEvent = Assert.IsAssignableFrom<RoutedEvent>(child.GetType().GetField("InvokedEvent")!.GetValue(null));
        child.RaiseEvent(new RoutedEventArgs(routedEvent));
        Assert.Equal(1, result.Root.GetType().GetField("Raised")!.GetValue(result.Root));
    }

    [AvaloniaTheory]
    [InlineData("Text", "Title")]
    [InlineData("Te&#120;t", "Title")]
    [InlineData("Path=Text", "Path=Title")]
    [InlineData("Path=Te&#120;t", "Path=Title")]
    [InlineData("Path=\"Te&#120;t\"", "Path=\"Title\"")]
    [InlineData("Pa&#116;h = \"Te&#120;t\"", "Pa&#116;h = \"Title\"")]
    public void Compiled_binding_member_rename_updates_path_and_executes_new_accessor(string spelling, string expected)
    {
        var xaml = $"<TextBlock {Ns} x:DataType='m:ViewModel' Text='{{CompiledBinding {spelling}}}'/>";
        var result = Rename(xaml, "Code.cs", Code.IndexOf("Text {", StringComparison.Ordinal), "Title");
        Assert.Contains("{CompiledBinding " + expected + "}", result.Xaml);
        var text = Assert.IsType<TextBlock>(result.Root);
        text.DataContext = Activator.CreateInstance(result.Assembly.GetType("Model.ViewModel")!);
        Assert.Equal("bound", text.Text);
    }

    [AvaloniaFact]
    public void Compiled_binding_path_element_keeps_whitespace_and_entity_boundaries()
    {
        var xaml = $"<TextBlock {Ns} x:DataType='m:ViewModel'><TextBlock.Text><CompiledBinding><CompiledBinding.Path>\r\n  Te&#120;t\r\n</CompiledBinding.Path></CompiledBinding></TextBlock.Text></TextBlock>";
        var result = Rename(xaml, "Code.cs", Code.IndexOf("Text {", StringComparison.Ordinal), "Title");
        Assert.Contains("<CompiledBinding.Path>\r\n  Title\r\n</CompiledBinding.Path>", result.Xaml);
        var text = Assert.IsType<TextBlock>(result.Root);
        text.DataContext = Activator.CreateInstance(result.Assembly.GetType("Model.ViewModel")!);
        Assert.Equal("bound", text.Text);
    }

    private static (string Code, string Xaml, Assembly Assembly, object Root) Rename(string xaml, string path, int offset, string name)
    {
        var syntax = XamlSyntaxTree.Parse(xaml, "View.axaml", version: 12);
        var inputs = new[] { new XamlProjectDocument(syntax, "View.axaml") };
        var application = CSharpCompilation.Create("Rename_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Code, new CSharpParseOptions(LanguageVersion.Preview), "Code.cs")], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var profile = AvaloniaFrameworkProfile.Create(createSourceInfo: true);
        var compiler = new XamlCompilationSession(application, profile, projectDocuments: inputs);
        var project = compiler.CompileProject([syntax], TestContext.Current.CancellationToken);
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(document => document.Output.Diagnostics)));
        var complete = XamlCSharpCompilation.AddGeneratedSources(application, project, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(complete.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var service = new XamlProjectRenameService(compiler, project, complete, ["Code.cs"]);
        var plan = service.Rename(path, offset, name, TestContext.Current.CancellationToken);
        string Apply(string file, string source)
        {
            var changes = plan.Documents.Single(document => document.Path == file);
            return Microsoft.CodeAnalysis.Text.SourceText.From(source).WithChanges(changes.Changes.Select(change =>
                new Microsoft.CodeAnalysis.Text.TextChange(new(change.Span.Start, change.Span.Length), change.NewText))).ToString();
        }
        var updatedCode = Apply("Code.cs", Code); var updatedXaml = Apply("View.axaml", xaml);
        var updatedApplication = application.RemoveAllSyntaxTrees().AddSyntaxTrees(CSharpSyntaxTree.ParseText(updatedCode, new CSharpParseOptions(LanguageVersion.Preview), "Code.cs"));
        var updatedSyntax = XamlSyntaxTree.Parse(updatedXaml, "View.axaml");
        var updated = new XamlProjectCompiler().Compile([new XamlProjectDocument(updatedSyntax, "View.axaml")], updatedApplication, profile, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(updated.Success, string.Join("\n", updated.Documents.SelectMany(document => document.Output.Diagnostics)));
        using var image = new MemoryStream();
        var emitted = XamlCSharpCompilation.AddGeneratedSources(updatedApplication, updated, cancellationToken: TestContext.Current.CancellationToken).Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = ResourceProjectFixture.Load(image.ToArray()); var output = Assert.Single(updated.Documents).Output;
        var root = assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null })!;
        return (updatedCode, updatedXaml, assembly, root);
    }
}
