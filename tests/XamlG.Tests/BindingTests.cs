using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;
namespace XamlG.Tests;

public sealed class BindingTests
{
    internal const string Model = """
        using System; using System.Collections.Generic;
        namespace Demo {
          [AttributeUsage(AttributeTargets.Property)] public class ContentAttribute : Attribute {}
          public class View { [Content] public List<Item> Children {get;} = new(); public string Title {get;set;} = string.Empty; public int Count {get;set;} public object Value {get;set;} public Type Type {get;set;} }
          public class Item { public string Text {get;set;} public string Name {get;set;} public int Value {get;set;} }
          public static class Layout { public static void SetRow(Item item, int row) {} public static int GetRow(Item item) => 0; }
          public class EchoExtension { public EchoExtension(string value) { Value=value; } public string Value {get;set;} public string ProvideValue(IServiceProvider services) => Value; }
          public class Generic<T> { public T Value {get;set;} }
          public partial class Main : View { }
        }
        """;
    internal static XamlFrameworkProfile Profile => XamlFrameworkProfile.Portable with { TypeSystem = new XamlTypeSystemConfiguration { ContentAttributes = ImmutableArray.Create("Demo.ContentAttribute"), NamespaceMappings = ImmutableArray.Create(new XmlNamespaceMapping("urn:demo", "Demo", "TestAssembly")) } };
    private static BoundDocument Bind(string body) => new XamlCompiler().Bind(XamlSyntaxTree.Parse(body), CompilationFactory.Create(Model), Profile);
    [Fact]
    public void ResolvesTypedChildrenAttachedPropertiesAndMarkupExtensions()
    {
        var result = Bind("<View xmlns='urn:demo' Title='{Echo hello}' Count='42'><Item Text='one' Layout.Row='2'/><Item Text='two'/></View>");
        Assert.True(result.Success, string.Join(";", result.Diagnostics)); Assert.Equal(4, result.Root!.Assignments.Length);
        Assert.Contains(result.Symbols, s => s.Symbol is IMethodSymbol m && m.Name == "SetRow");
    }
    [Theory]
    [InlineData("Count='not-an-int'", "XG1008")]
    [InlineData("Unknown='x'", "XG1005")]
    [InlineData("Count='{x:Null}'", "XG1022")]
    public void InvalidSemanticsHaveSourceDiagnostics(string attribute, string code)
    { var result = Bind("<View xmlns='urn:demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + attribute + "/>"); Assert.False(result.Success); Assert.Contains(result.Diagnostics, d => d.Code == code); }
    [Fact]
    public void GenericTypesUseRoslynConstruction()
    {
        var result = Bind("<Generic xmlns='urn:demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:TypeArguments='x:Int32' Value='21'/>");
        Assert.True(result.Success, string.Join(";", result.Diagnostics)); Assert.Equal(SpecialType.System_Int32, result.Root!.Type.TypeArguments[0].SpecialType);
    }
    [Fact]
    public void CodeBehindUsesExistingCompilationIdentity()
    {
        var result = Bind("<View xmlns='urn:demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Demo.Main'/>");
        Assert.True(result.Success, string.Join(";", result.Diagnostics)); Assert.Equal("Demo.Main", result.ClassSymbol!.ToDisplayString());
    }
    [Fact]
    public void RejectsDuplicateScalarAssignments()
    { var result = Bind("<View xmlns='urn:demo' Title='one'><View.Title>two</View.Title></View>"); Assert.Contains(result.Diagnostics, d => d.Code == "XG1014"); }
    [Fact]
    public void TypeResolutionUsesAssemblyAndArity()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model), Profile.TypeSystem);
        Assert.NotNull(types.Resolve("clr-namespace:Demo;assembly=TestAssembly", "View").Type);
        Assert.Null(types.Resolve("clr-namespace:Demo;assembly=Missing", "View").Type);
        Assert.Null(types.Resolve("urn:demo", "Generic").Type); Assert.NotNull(types.Resolve("urn:demo", "Generic", 1).Type);
    }
}
