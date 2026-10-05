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

internal sealed class LoaderAdapterFixture
{
    public const string Namespace = "xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private const string Abi = """
        namespace Model {
          public class Root { private string _text; public int Writes; public string Text {get => _text; set { _text=value; Writes++; }} public object Value {get;set;} }
          public class ServiceExtension { public object ProvideValue(System.IServiceProvider services) => services.GetService(typeof(string)); }
          public static class Loader {
            public static void Load(object instance) => throw new System.Exception("Original loader reached");
            public static void Load(System.IServiceProvider services, object instance) => throw new System.Exception("Original loader reached");
            public static object Load(System.Uri uri, System.Uri baseUri = null) => throw new System.Exception("Original URI loader reached");
            public static object Load(System.IServiceProvider services, System.Uri uri, System.Uri baseUri = null) => throw new System.Exception("Original URI loader reached");
          }
        }
        """;
    public LoaderAdapterFixture(string code, params (string Path, string Text)[] documents)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        Compilation = CSharpCompilation.Create("LoaderTests_" + Guid.NewGuid().ToString("N"), new[]
        { CSharpSyntaxTree.ParseText(code + "\n" + Abi, new CSharpParseOptions(LanguageVersion.Preview), "Code.cs") },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Profile = XamlFrameworkProfile.Portable with { SourceLoader = new("Model.Loader", "Load") };
        Project = new XamlProjectCompiler().Compile(documents.Select(d => new XamlProjectDocument(XamlSyntaxTree.Parse(d.Text, d.Path), d.Path)), Compilation, Profile);
    }
    public CSharpCompilation Compilation { get; }
    public XamlFrameworkProfile Profile { get; }
    public XamlProjectCompilation Project { get; }
    public Assembly Emit()
    {
        Assert.True(Project.Success, string.Join("\n", Project.Documents.SelectMany(d => d.Output.Diagnostics)) + "\n" + string.Join("\n", Project.SourceIntegration.Diagnostics));
        var complete = XamlCSharpCompilation.AddGeneratedSources(Compilation, Project);
        using var stream = new MemoryStream(); var emitted = complete.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics) + "\n" + string.Join("\n", XamlCSharpCompilation.Sources(Project).Select(s => s.Source)));
        return Assembly.Load(stream.ToArray());
    }
    public object Build(string path, IServiceProvider? services = null)
    {
        var assembly = Emit(); var output = Project.Documents.Single(d => d.Input.LogicalPath == path).Output;
        return assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { services })!;
    }
}
