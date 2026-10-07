using System.Collections;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class CollectionAssignmentTests
{
    private const string Model = """
        using System.Collections.Generic;
        namespace Demo;
        public class View
        {
            private Strings _items = new() { "original" };
            public Strings Original { get; }
            public int Replacements { get; private set; }
            public View() => Original = _items;
            public Strings Items { get => _items; set { _items = value; Replacements++; } }
            public List<object> Objects { get; set; } = new();
        }
        public class Strings : List<string> { }
        public class ReplacementExtension
        {
            public Strings ProvideTypedValue() => new() { "provided" };
        }
        """;

    [Theory]
    [InlineData("<Strings><x:String>replacement</x:String></Strings>", "replacement")]
    [InlineData("<ReplacementExtension/>", "provided")]
    public void ReplacesCollectionBeforeAddingSubsequentItems(string replacement, string expected)
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Items>
            """ + replacement + """
                <x:String>appended</x:String>
              </View.Items>
            </View>
            """, Model);
        var root = code.Build();
        Assert.Equal(new[] { expected, "appended" }, Items(root, "Items"));
        Assert.Equal(new[] { "original" }, Items(root, "Original"));
        Assert.Equal(1, root.GetType().GetProperty("Replacements")!.GetValue(root));
    }

    [Fact]
    public void CompatibleCollectionCanBeAnItemAfterTheFirstValue()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:generic="using:System.Collections.Generic">
              <View.Objects>
                <generic:List x:TypeArguments="x:Object"/>
                <generic:List x:TypeArguments="x:Object"><x:String>nested</x:String></generic:List>
              </View.Objects>
            </View>
            """, Model);
        var root = code.Build();
        var nested = Assert.Single(((IEnumerable)root.GetType().GetProperty("Objects")!.GetValue(root)!).Cast<object>());
        Assert.Equal("nested", Assert.Single(((IEnumerable)nested).Cast<object>()));
    }

    [Fact]
    public void IncompatibleSecondReplacementIsDiagnosed()
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("""
            <View xmlns="clr-namespace:Demo"><View.Items><Strings/><Strings/></View.Items></View>
            """), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1016");
    }

    private static string[] Items(object root, string property) =>
        ((IEnumerable)root.GetType().GetProperty(property)!.GetValue(root)!).Cast<string>().ToArray();
}
