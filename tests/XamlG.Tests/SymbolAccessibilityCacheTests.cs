using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using Xunit;

namespace XamlG.Tests;

public sealed class SymbolAccessibilityCacheTests
{
    private const string Model = """
        public class Base<T> {
            public T Public {get;set;} protected T Protected {get;set;}
            internal T Internal {get;set;} private T Private {get;set;}
            private class Hidden { public int Member {get;set;} }
            public class Nested { }
        }
        public class Derived : Base<string> { }
        public class Other { }
        """;
    [Fact]
    public void Cached_results_match_Roslyn_for_each_member_and_access_context_under_concurrency()
    {
        var compilation = CompilationFactory.Create(Model);
        var types = new RoslynTypeSystem(compilation);
        var open = compilation.GetTypeByMetadataName("Base`1")!;
        var closed = open.Construct(compilation.GetSpecialType(SpecialType.System_String));
        var contexts = new INamedTypeSymbol?[] { null, open, closed,
            compilation.GetTypeByMetadataName("Derived"), compilation.GetTypeByMetadataName("Other"), open.GetTypeMembers("Nested").Single() };
        var symbols = open.GetMembers().Concat(closed.GetMembers()).Concat(open.GetTypeMembers("Hidden").Single().GetMembers()).ToArray();
        var expected = contexts.SelectMany(within => symbols.Select(symbol => (symbol, within,
            result: compilation.IsSymbolAccessibleWithin(symbol, (ISymbol?)within ?? compilation.Assembly)))).ToArray();
        Parallel.For(0, 32, _ =>
        {
            foreach (var entry in expected) Assert.Equal(entry.result, types.IsAccessible(entry.symbol, entry.within));
        });
    }

    [Fact]
    public void Saturation_and_a_new_compilation_do_not_change_accessibility()
    {
        var source = "public class Large {" + string.Concat(Enumerable.Range(0, 17000).Select(i =>
            (i % 2 == 0 ? "public" : "private") + " int F" + i + ";")) + "}";
        var compilation = CompilationFactory.Create(source);
        var types = new RoslynTypeSystem(compilation);
        var owner = compilation.GetTypeByMetadataName("Large")!;
        foreach (var symbol in owner.GetMembers().OfType<IFieldSymbol>())
        {
            var expected = compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly);
            Assert.Equal(expected, types.IsAccessible(symbol));
            Assert.Equal(expected, types.IsAccessible(symbol));
            Assert.True(types.IsAccessible(symbol, owner));
        }
        var next = CompilationFactory.Create("public class Large { private int F0; }");
        Assert.False(new RoslynTypeSystem(next).IsAccessible(next.GetTypeByMetadataName("Large")!.GetMembers("F0").Single()));
        Assert.True(types.IsAccessible(owner.GetMembers("F0").Single()));
    }
}
