using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
using Xunit;

namespace XamlG.Tests;

public sealed class NamespaceResolutionTests
{
    [Theory]
    [InlineData("urn:library")]
    [InlineData("clr-namespace:ResolutionModel;assembly= LibraryA , Version=1.0.0.0, Culture=neutral")]
    public void SharedNamespacePlanPreservesAssemblySelectionArityAndMissingLookups(string xmlNamespace)
    {
        var library = Reference("LibraryA", "namespace ResolutionModel { public class Item {} public class Box<T> {} }");
        var other = Reference("LibraryB", "namespace ResolutionModel { public class Item {} public class Other {} }");
        var compilation = CompilationFactory.Create("").AddReferences(library, other);
        var types = new RoslynTypeSystem(compilation, new()
        {
            NamespaceMappings = [new("urn:library", "ResolutionModel", " LibraryA , Version=1.0.0.0, Culture=neutral")]
        });
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal("LibraryA", types.Resolve(xmlNamespace, "Item").Type!.ContainingAssembly.Name);
            Assert.Equal(1, types.Resolve(xmlNamespace, "Box", 1).Type!.Arity);
            Assert.Null(types.Resolve(xmlNamespace, "Box").Type);
            Assert.Null(types.Resolve(xmlNamespace, "Other").Type);
            Assert.Null(types.Resolve(xmlNamespace, "Missing").Type);
        });
    }

    [Fact]
    public void UntargetedMappingsRetainAmbiguityAndDeduplicateOverlappingMappings()
    {
        var first = Reference("LibraryA", "namespace ResolutionModel { public class Item {} }");
        var second = Reference("LibraryB", "namespace ResolutionModel { public class Item {} }");
        var types = new RoslynTypeSystem(CompilationFactory.Create("").AddReferences(first, second), new()
        {
            NamespaceMappings = [new("urn:both", "ResolutionModel"), new("urn:both", "ResolutionModel", "LibraryA")]
        });
        foreach (var xmlNamespace in new[] { "using:ResolutionModel", "urn:both" })
        {
            var result = types.Resolve(xmlNamespace, "Item");
            Assert.Null(result.Type);
            Assert.Equal(new[] { "LibraryA", "LibraryB" }, result.Candidates.Select(type => type.ContainingAssembly.Name));
        }
        Assert.Null(types.Resolve("clr-namespace:ResolutionModel;assembly=librarya", "Item").Type);
    }

    [Fact]
    public void DefaultAssemblyAndNamespaceCachesRemainCompilationScoped()
    {
        var library = Reference("LibraryA", "namespace ResolutionModel { public class Item {} }");
        var compilation = CompilationFactory.Create("namespace ResolutionModel { public class Item {} }").AddReferences(library);
        var local = new RoslynTypeSystem(compilation);
        var configured = new RoslynTypeSystem(compilation, new() { DefaultAssemblyName = "LibraryA" });
        Assert.Equal("TestAssembly", local.Resolve("clr-namespace:ResolutionModel", "Item").Type!.ContainingAssembly.Name);
        Assert.Equal("LibraryA", configured.Resolve("clr-namespace:ResolutionModel", "Item").Type!.ContainingAssembly.Name);
        Assert.Null(local.Resolve("clr-namespace:ResolutionModel", "Added").Type);
        var replacement = new RoslynTypeSystem(CompilationFactory.Create("namespace ResolutionModel { public class Added {} }"));
        Assert.NotNull(replacement.Resolve("clr-namespace:ResolutionModel", "Added").Type);
        Assert.Null(replacement.Resolve("clr-namespace:ResolutionModel", "Item").Type);
    }

    [Fact]
    public void AssemblyTargetedMappingsResolveForwardersToTheirDestinationIdentity()
    {
        var library = Reference("LibraryA", "namespace ResolutionModel { public class Item {} public class Box<T> {} }");
        var facade = Reference("Facade", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ResolutionModel.Item))] " +
            "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(ResolutionModel.Box<>))]", library);
        var types = new RoslynTypeSystem(CompilationFactory.Create("").AddReferences(library, facade));
        Assert.Equal("LibraryA", types.Resolve("clr-namespace:ResolutionModel;assembly=Facade", "Item").Type!.ContainingAssembly.Name);
        Assert.Equal("LibraryA", types.Resolve("clr-namespace:ResolutionModel;assembly=Facade", "Box", 1).Type!.ContainingAssembly.Name);
    }

    private static PortableExecutableReference Reference(string name, string source, params MetadataReference[] references)
    {
        CSharpCompilation compilation = CompilationFactory.Create(source).WithAssemblyName(name).AddReferences(references);
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }
}
