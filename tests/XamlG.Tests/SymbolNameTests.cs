using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using Xunit;

namespace XamlG.Tests;

public sealed class SymbolNameTests
{
    [Fact]
    public void NamesPreserveNestedGenericAndEscapedSymbolsAcrossConcurrentReads()
    {
        var compilation = CompilationFactory.Create("namespace @event { public class Outer<T> { public class Inner<U> { public int @class {get;set;} } } }");
        var outer = compilation.GetTypeByMetadataName("event.Outer`1")!.Construct(compilation.GetSpecialType(SpecialType.System_String));
        var inner = outer.GetTypeMembers("Inner").Single().Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        var expected = inner.OriginalDefinition.ContainingType!.ContainingNamespace.ToDisplayString() + ".Outer`1+Inner`1";
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal("global::@event.Outer<string>.Inner<int>", inner.CSharpName());
            Assert.Equal(expected, inner.MetadataName()); Assert.True(inner.HasMetadataName(expected));
            Assert.False(inner.HasMetadataName("wrong.Outer`1+Inner`1"));
            Assert.False(inner.HasMetadataName("event.Inner`1"));
        });
    }

    [Fact]
    public void NewCompilationsDoNotReuseOldSymbolAttributes()
    {
        var first = CompilationFactory.Create("[System.Obsolete] public class View {} ").GetTypeByMetadataName("View")!;
        var second = CompilationFactory.Create("public class View {} ").GetTypeByMetadataName("View")!;
        Assert.True(first.HasAttribute(new[] { "System.ObsoleteAttribute" }));
        Assert.False(second.HasAttribute(new[] { "System.ObsoleteAttribute" }));
        Assert.Equal("View", first.MetadataName()); Assert.Equal("View", second.MetadataName());
    }
}
