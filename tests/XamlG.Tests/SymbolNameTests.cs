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

    [Fact]
    public void CachedMembersPreserveHidingAndInterfaceOrderAcrossConcurrentReads()
    {
        const string source = "public class Base { public int Value; } public class Derived : Base { public new string Value; } " +
            "public interface IBase { int Value {get;} } public interface ILeft : IBase { int Left {get;} } " +
            "public interface IRight : IBase { int Right {get;} } public interface IChild : ILeft, IRight { new string Value {get;} }";
        var compilation = CompilationFactory.Create(source);
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var contract = compilation.GetTypeByMetadataName("IChild")!;
        var expectedContractMembers = contract.GetMembers().Concat(contract.AllInterfaces.SelectMany(type => type.GetMembers())).ToArray();
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(new[] { "Derived", "Base" }, derived.Members("Value").Select(member => member.ContainingType.Name));
            Assert.Equal(new[] { "IChild", "IBase" }, contract.Members("Value").Select(member => member.ContainingType.Name));
            Assert.Equal(expectedContractMembers, contract.Members());
            Assert.Empty(derived.Members("Missing"));
        });
        var replacement = CompilationFactory.Create("public class Derived { public bool Value; public int Missing; }").GetTypeByMetadataName("Derived")!;
        Assert.Single(replacement.Members("Value"));
        Assert.Single(replacement.Members("Missing"));
        Assert.Equal(2, derived.Members("Value").Count());
        Assert.Empty(derived.Members("Missing"));
    }

    [Fact]
    public void MetadataLookupCachesDoNotCrossCompilationRevisions()
    {
        var first = new RoslynTypeSystem(CompilationFactory.Create("public class Before {}"));
        var second = new RoslynTypeSystem(CompilationFactory.Create("public class After {}"));
        for (var i = 0; i < 2; i++)
        {
            Assert.NotNull(first.Find("Before")); Assert.Null(first.Find("After"));
            Assert.Null(second.Find("Before")); Assert.NotNull(second.Find("After"));
        }
    }
}
