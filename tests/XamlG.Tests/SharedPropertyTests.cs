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
}
