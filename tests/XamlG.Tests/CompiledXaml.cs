using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;
namespace XamlG.Tests;

internal sealed class CompiledXaml : IDisposable
{
    private readonly AssemblyLoadContext _loadContext = new("XamlG.Execution." + Guid.NewGuid(), isCollectible: true);
    private CompiledXaml(XamlEmissionResult emission, byte[] assembly)
    {
        Emission = emission;
        _loadContext.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
        using var input = new MemoryStream(assembly); Assembly = _loadContext.LoadFromStream(input);
    }
    public Assembly Assembly { get; }
    public XamlEmissionResult Emission { get; }
    public object Build(IServiceProvider? services = null) => Assembly.GetType(Emission.FactoryMetadataName)!.GetMethod(Emission.BuildMethodName!)!.Invoke(null, new object?[] { services })!;
    public static CompiledXaml Create(string xaml, string model, XamlFrameworkProfile? profile = null, bool shareAcrossDocuments = false, XamlCompilerOptions? options = null)
    {
        var compilation = CompilationFactory.Create(model).WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        if (!compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location))
            compilation = compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        if (shareAcrossDocuments)
        {
            var inputs = new[] { "Test.axaml", "Other.axaml" }.Select(path => new XamlProjectDocument(XamlSyntaxTree.Parse(xaml, path), path));
            var project = new XamlProjectCompiler().Compile(inputs, compilation, profile, options);
            Assert.True(project.Success, string.Join(Environment.NewLine, project.Documents.SelectMany(document => document.Output.Diagnostics)));
            using var stream = new MemoryStream();
            var emitted = XamlCSharpCompilation.AddGeneratedSources(compilation, project).Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return new(project.Documents.Single(document => document.Input.LogicalPath == "Test.axaml").Output, stream.ToArray());
        }
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Test.axaml"), compilation, profile, options);
        Assert.True(document.Success, string.Join(Environment.NewLine, document.Diagnostics));
        var emission = new CSharpEmitter().Emit(document);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics));
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(emission.Source, new CSharpParseOptions(LanguageVersion.Preview), emission.HintName));
        using var output = new MemoryStream(); var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + Environment.NewLine + emission.Source);
        return new(emission, output.ToArray());
    }
    public void Dispose() => _loadContext.Unload();
}
