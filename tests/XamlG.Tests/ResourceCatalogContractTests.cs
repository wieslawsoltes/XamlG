using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ResourceCatalogContractTests
{
    [Fact]
    public void PrivateNestedCodeBehindRootsAreNotAddressableByUnrelatedLocalFactories()
    {
        var compilation = Compilation("namespace Model { public class Base { } public partial class Outer { private partial class View : Base { } } }");
        var input = new XamlProjectDocument(XamlSyntaxTree.Parse("<Base xmlns='clr-namespace:Model' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Model.Outer+View'/>", "Private.xaml"), "Private.xaml");
        var project = new XamlProjectCompiler().Compile(new[] { input }, compilation);
        Assert.True(project.Success, string.Join("\n", project.Documents.SelectMany(d => d.Output.Diagnostics)));
        Assert.Empty(project.Resources.Resources);
        Assert.NotNull(project.Documents[0].Output.BuildMethodName); // The class can still construct itself.
        Assert.DoesNotContain("XamlCompiledResourceAttribute", project.Documents[0].Output.Source);
    }

    [Theory]
    [InlineData("public static class Factory<T> { public static Root Build(System.IServiceProvider services) => new Root(); }", "Model.Factory<>")]
    [InlineData("public static class Factory { public static Root Build(ref System.IServiceProvider services) => new Root(); }", "Model.Factory")]
    [InlineData("public static class Factory { private static Root Value = new Root(); public static ref Root Build(System.IServiceProvider services) => ref Value; }", "Model.Factory")]
    public void MetadataExportsCannotSmuggleUnboundOrByReferenceFactorySignatures(string declaration, string factoryType)
    {
        var library = Compilation("[assembly: XamlG.Runtime.XamlCompiledResourceAttribute(\"xamlg://producer/Resource.xaml\", typeof(" + factoryType + "), \"Build\")] namespace Model { public class Root { } " + declaration + " }");
        using var image = new MemoryStream();
        var emitted = library.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var consumer = Compilation("internal class Consumer { }").AddReferences(MetadataReference.CreateFromImage(image.ToArray()));
        Assert.Empty(new XamlProjectCompiler().Compile(Array.Empty<XamlProjectDocument>(), consumer).Resources.Resources);
    }

    private static CSharpCompilation Compilation(string source)
    {
        var compilation = CompilationFactory.Create(source);
        return compilation.References.OfType<PortableExecutableReference>().Any(r => r.FilePath == typeof(XamlRuntimeContext).Assembly.Location)
            ? compilation : compilation.AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
    }
}
