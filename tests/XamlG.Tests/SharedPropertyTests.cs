using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class SharedPropertyTests
{
    private const string Model = """
        namespace Shared {
          public class Node { public string Text {get;set;} public int Number {get;set;} }
          public partial class View : Node { private string Secret {get;set;} public string ReadSecret => Secret; }
        }
        public partial class GlobalView : Shared.Node { private string Secret {get;set;} public string ReadSecret => Secret; }
        """;
    private static CSharpCompilation Compilation()
    {
        var compilation = CompilationFactory.Create(Model);
        return compilation.References.OfType<PortableExecutableReference>().Any(reference => reference.FilePath == typeof(XamlRuntimeContext).Assembly.Location)
            ? compilation : compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
    }
    private static XamlProjectDocument Document(string path, string attributes) => new(XamlSyntaxTree.Parse(
        "<Node xmlns='clr-namespace:Shared' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + attributes + "/>", path), path);
    private static Assembly Emit(CSharpCompilation compilation, XamlProjectCompilation project)
    {
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(document => document.Output.Diagnostics)));
        using var stream = new MemoryStream();
        var emitted = XamlCSharpCompilation.AddGeneratedSources(compilation, project).Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
    private static object Build(Assembly assembly, XamlProjectCompilation project, string path)
    {
        var output = project.Documents.Single(document => document.Input.LogicalPath == path).Output;
        return assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null })!;
    }
    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static void Update(object root, string name, object value)
    {
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        Assert.True(session!.Apply(session.Revision, new[] { new XamlPropertyUpdate(session.FindNode(root)!.Key, name, value) }).Applied);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void InheritedAccessorsShareAcrossDerivedOwnersWithoutMergingDistinctMembers(int concurrency)
    {
        const string model = """
            namespace Shared {
              public class Base { public virtual string Text { get; set; } }
              public class First : Base { }
              public class Second : Base { }
              public class Override : Base {
                public override string Text { get => base.Text; set => base.Text = "override:" + value; }
              }
              public class Hidden : Base { public new string Text { get; set; } }
              public class Slot<T> { public T Value { get; set; } }
              public class TextSlot : Slot<string> { }
              public class NumberSlot : Slot<int> { }
            }
            """;
        var compilation = CompilationFactory.Create(model).AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        static XamlProjectDocument Input(string type, string property, string value) => new(XamlSyntaxTree.Parse(
            "<" + type + " xmlns='clr-namespace:Shared' " + property + "='" + value + "'/>", type + ".xaml"), type + ".xaml");
        var documents = new[] { Input("First", "Text", "first"), Input("Second", "Text", "second"),
            Input("Override", "Text", "initial"), Input("Hidden", "Text", "hidden"),
            Input("TextSlot", "Value", "text"), Input("NumberSlot", "Value", "42") };
        var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var project = compiler.Compile(documents, compilation, options: options);
        var assembly = Emit(compilation, project);
        var first = Build(assembly, project, "First.xaml");
        var second = Build(assembly, project, "Second.xaml");
        var overridden = Build(assembly, project, "Override.xaml");
        var hidden = Build(assembly, project, "Hidden.xaml");
        var text = Build(assembly, project, "TextSlot.xaml");
        var number = Build(assembly, project, "NumberSlot.xaml");
        Update(first, "Text", "edited first"); Update(second, "Text", "edited second");
        Update(overridden, "Text", "edited"); Update(hidden, "Text", "edited hidden");
        Update(text, "Value", "edited text"); Update(number, "Value", 123);
        Assert.Equal("edited first", Property(first, "Text"));
        Assert.Equal("edited second", Property(second, "Text"));
        Assert.Equal("override:edited", Property(overridden, "Text"));
        Assert.Equal("edited hidden", Property(hidden, "Text"));
        Assert.Null(assembly.GetType("Shared.Base")!.GetProperty("Text")!.GetValue(hidden));
        Assert.Equal("edited text", Property(text, "Value")); Assert.Equal(123, Property(number, "Value"));
        var sources = string.Join("\n", project.Documents.Select(document => document.Output.Source));
        Assert.Equal(2, sources.Split("((global::Shared.Base)__target).@Text").Length - 1); // One getter and setter.
        var changed = compiler.Compile(documents.Skip(1), compilation, options: options);
        var survivor = Build(Emit(compilation, changed), changed, "Second.xaml");
        Update(survivor, "Text", "survived"); Assert.Equal("survived", Property(survivor, "Text"));
        var fresh = new XamlProjectCompiler().Compile(documents.Skip(1), compilation, options: options);
        Assert.Equal(fresh.Documents.Select(document => document.Output.Source), changed.Documents.Select(document => document.Output.Source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ChangedTableLayoutsReuseBindingsAndMatchFreshBuilds(int concurrency)
    {
        var compilation = Compilation(); var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var first = Document("First.xaml", "Text='first'");
        var second = Document("Second.xaml", "Text='second'");
        var initial = compiler.Compile(new[] { first, second }, compilation, options: options);
        Emit(compilation, initial);
        var edited = Document(first.LogicalPath, "Text='edited' Number='3'");
        var changed = compiler.Compile(new[] { edited, second }, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(1, 1, 2, 0), changed.Statistics);
        var fresh = new XamlProjectCompiler().Compile(new[] { edited, second }, compilation, options: options);
        Assert.Equal(fresh.Documents.Select(document => document.Output.Source), changed.Documents.Select(document => document.Output.Source));
        var assembly = Emit(compilation, changed);
        var firstRoot = Build(assembly, changed, first.LogicalPath);
        var secondRoot = Build(assembly, changed, second.LogicalPath);
        Update(firstRoot, "Number", 4); Update(firstRoot, "Text", "changed first"); Update(secondRoot, "Text", "changed second");
        Assert.Equal(4, Property(firstRoot, "Number")); Assert.Equal("changed first", Property(firstRoot, "Text"));
        Assert.Equal(0, Property(secondRoot, "Number")); Assert.Equal("changed second", Property(secondRoot, "Text"));

        var recovered = compiler.Compile(new[] { first, second }, compilation, options: options);
        Emit(compilation, recovered);
        Assert.Equal(initial.Documents.Select(document => document.Output.Source), recovered.Documents.Select(document => document.Output.Source));
        var removed = compiler.Compile(new[] { second }, compilation, options: options);
        var survivor = Build(Emit(compilation, removed), removed, second.LogicalPath);
        Update(survivor, "Text", "survived"); Assert.Equal("survived", Property(survivor, "Text"));
    }

    [Theory]
    [InlineData("Shared.View")]
    [InlineData("GlobalView")]
    public void PrivateCodeBehindAccessorsRemainInTheirDeclaringClass(string className)
    {
        var compilation = Compilation();
        var document = Document("View.xaml", "x:Class='" + className + "' Text='public' Secret='private'");
        var other = new XamlProjectDocument(XamlSyntaxTree.Parse(className == "Shared.View"
            ? "<View xmlns='clr-namespace:Shared' Text='other'/>"
            : "<GlobalView xmlns='clr-namespace:' Text='other'/>", "Other.xaml"), "Other.xaml");
        var project = new XamlProjectCompiler().Compile(new[] { document, other }, compilation);
        var root = Build(Emit(compilation, project), project, document.LogicalPath);
        Assert.Equal("private", Property(root, "ReadSecret"));
        Update(root, "Secret", "changed private"); Update(root, "Text", "changed public");
        Assert.Equal("changed private", Property(root, "ReadSecret")); Assert.Equal("changed public", Property(root, "Text"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void GeneralSetterLayoutChangesInvalidateCallersAndSurviveOwnerRemoval(int concurrency)
    {
        var compilation = CompilationFactory.Create("namespace SetterLayout; public class Node { public object Value { get; set; } } public class Payload { public string Text { get; set; } }")
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        static XamlProjectDocument Input(string path, string value, bool child = false) => new(XamlSyntaxTree.Parse(
            "<Node xmlns='clr-namespace:SetterLayout' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Node.Value>" +
            (child ? "<Payload Text='" + value + "'/>" : "<x:String>" + value + "</x:String>") + "</Node.Value></Node>", path), path);
        var documents = new[] { Input("First.xaml", "first"), Input("Second.xaml", "second"), Input("Third.xaml", "third") };
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var compiler = new XamlProjectCompiler();
        var initial = compiler.Compile(documents, compilation, options: options);
        Assert.DoesNotContain(".Assign", string.Join("\n", initial.Documents.Select(document => document.Output.Source)), StringComparison.Ordinal);
        documents[0] = Input("First.xaml", "first child", child: true);
        documents[1] = Input("Second.xaml", "second child", child: true);
        var changed = compiler.Compile(documents, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(2, 1, 3, 0), changed.Statistics);
        var fresh = new XamlProjectCompiler().Compile(documents, compilation, options: options);
        Assert.Equal(fresh.Documents.Select(document => document.Output.Source), changed.Documents.Select(document => document.Output.Source));
        Assert.Contains(".Assign", string.Join("\n", changed.Documents.Select(document => document.Output.Source)), StringComparison.Ordinal);
        var assembly = Emit(compilation, changed);
        var first = Build(assembly, changed, "First.xaml");
        Assert.Equal("first child", Property(Property(first, "Value")!, "Text"));
        Update(first, "Value", "edited"); Assert.Equal("edited", Property(first, "Value"));
        Assert.Equal("third", Property(Build(assembly, changed, "Third.xaml"), "Value"));
        var survivor = compiler.Compile(documents.Skip(1), compilation, options: options);
        fresh = new XamlProjectCompiler().Compile(documents.Skip(1), compilation, options: options);
        Assert.Equal(fresh.Documents.Select(document => document.Output.Source), survivor.Documents.Select(document => document.Output.Source));
        assembly = Emit(compilation, survivor);
        var second = Build(assembly, survivor, "Second.xaml");
        Assert.Equal("second child", Property(Property(second, "Value")!, "Text"));
        Update(second, "Value", "survived"); Assert.Equal("survived", Property(second, "Value"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ScalarHelperLayoutChangesInvalidateCallersWithoutChangingTheEditingTable(int concurrency)
    {
        var compilation = CompilationFactory.Create("namespace ScalarLayout; public class Node { public object Value { get; set; } }")
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        static XamlProjectDocument Input(string path, string type, string value) => new(XamlSyntaxTree.Parse(
            "<Node xmlns='clr-namespace:ScalarLayout' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<Node.Value><x:" + type + ">" + value + "</x:" + type + "></Node.Value></Node>", path), path);
        var documents = new[] { Input("First.xaml", "Int32", "1"), Input("Second.xaml", "Int32", "2"), Input("Third.xaml", "Int32", "3") };
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        var compiler = new XamlProjectCompiler();
        var initial = compiler.Compile(documents, compilation, options: options);
        var initialSource = string.Join("\n", initial.Documents.Select(document => document.Output.Source));
        Assert.Equal(1, initialSource.Split("internal static void SetScalar", StringSplitOptions.None).Length - 1);
        var assembly = Emit(compilation, initial);
        Assert.Equal(3, Property(Build(assembly, initial, "Third.xaml"), "Value"));

        documents[0] = Input("First.xaml", "String", "first");
        documents[1] = Input("Second.xaml", "String", "second");
        var changed = compiler.Compile(documents, compilation, options: options);
        Assert.Equal(new XamlProjectStatistics(2, 1, 3, 0), changed.Statistics);
        var fresh = new XamlProjectCompiler().Compile(documents, compilation, options: options);
        Assert.Equal(fresh.Documents.Select(document => document.Output.Source), changed.Documents.Select(document => document.Output.Source));
        var changedSource = string.Join("\n", changed.Documents.Select(document => document.Output.Source));
        Assert.Equal(1, changedSource.Split("internal static void SetScalar", StringSplitOptions.None).Length - 1);
        assembly = Emit(compilation, changed);
        var first = Build(assembly, changed, "First.xaml");
        var third = Build(assembly, changed, "Third.xaml");
        Assert.Equal("first", Property(first, "Value")); Assert.Equal(3, Property(third, "Value"));
        Update(first, "Value", "edited"); Update(third, "Value", 4);
        Assert.Equal("edited", Property(first, "Value")); Assert.Equal(4, Property(third, "Value"));

        var survivor = compiler.Compile(documents.Skip(1), compilation, options: options);
        assembly = Emit(compilation, survivor);
        Assert.Equal("second", Property(Build(assembly, survivor, "Second.xaml"), "Value"));
        Assert.Equal(3, Property(Build(assembly, survivor, "Third.xaml"), "Value"));
    }
}
