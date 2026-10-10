using Microsoft.CodeAnalysis;
using XamlG.Frameworks.Avalonia;
using XamlG.Roslyn;
using Xunit;

namespace XamlG.Tests;

public sealed class TypeHierarchyCacheTests
{
    private static readonly Func<ITypeSymbol, string, bool> Is = typeof(AvaloniaFrameworkProfile).Assembly
        .GetType("XamlG.Frameworks.Avalonia.Styling.AvaloniaStyleScope")!.GetMethod("Is")!
        .CreateDelegate<Func<ITypeSymbol, string, bool>>();

    [Fact]
    public void NamedGenericNestedArrayAndInterfaceTypesRetainClassificationUnderConcurrentQueries()
    {
        var compilation = CompilationFactory.Create("""
            namespace Hierarchy {
                public interface IBase<T> { }
                public interface IDerived : IBase<int> { }
                public class Base<T> : IDerived { }
                public class Outer<T> { public class Nested<U> : Base<U> { } }
                public class Leaf : Outer<int>.Nested<string> { }
                public class SameName { }
            }
            namespace Other { public class SameName { } }
            """);
        var leaf = compilation.GetTypeByMetadataName("Hierarchy.Leaf")!;
        ITypeSymbol[] types = [leaf, leaf.BaseType!, compilation.GetTypeByMetadataName("Hierarchy.Base`1")!,
            compilation.GetTypeByMetadataName("Hierarchy.IDerived")!, compilation.GetTypeByMetadataName("Hierarchy.SameName")!,
            compilation.GetTypeByMetadataName("Other.SameName")!, compilation.CreateArrayTypeSymbol(leaf),
            compilation.GetSpecialType(SpecialType.System_String), compilation.DynamicType,
            compilation.GetTypeByMetadataName("Hierarchy.Base`1")!.TypeParameters[0],
            compilation.CreateErrorTypeSymbol(null, "Missing", 0)];
        string[] names = ["Hierarchy.Leaf", "Hierarchy.Base`1", "Hierarchy.Outer`1+Nested`1", "Hierarchy.IDerived", "Hierarchy.IBase`1",
            "Hierarchy.SameName", "Other.SameName", "System.Object", "System.String", "System.Collections.IEnumerable",
            "System.Collections.Generic.IEnumerable`1", "hierarchy.Leaf", "Leaf", "", "Missing"];
        Parallel.For(0, 16, _ =>
        {
            foreach (var type in types)
                foreach (var name in names) Assert.Equal(Reference(type, name), Is(type, name));
        });
    }

    [Fact]
    public void SameNamedTypesFromDifferentCompilationsKeepDifferentBaseClassesAndInterfaces()
    {
        const string model = "namespace Hierarchy; public interface IContract {} public class Base {} public class Leaf : BASE {}";
        var old = CompilationFactory.Create(model.Replace("BASE", "Base, IContract", StringComparison.Ordinal)).GetTypeByMetadataName("Hierarchy.Leaf")!;
        var changed = CompilationFactory.Create(model.Replace("BASE", "object", StringComparison.Ordinal)).GetTypeByMetadataName("Hierarchy.Leaf")!;
        for (var i = 0; i < 10; i++)
        {
            Assert.True(Is(old, "Hierarchy.Base"));
            Assert.True(Is(old, "Hierarchy.IContract"));
            Assert.False(Is(changed, "Hierarchy.Base"));
            Assert.False(Is(changed, "Hierarchy.IContract"));
            Assert.True(Is(changed, "Hierarchy.Leaf"));
        }
    }

    private static bool Reference(ITypeSymbol type, string name)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            if (current.HasMetadataName(name)) return true;
        return type.AllInterfaces.Any(contract => contract.HasMetadataName(name));
    }
}
