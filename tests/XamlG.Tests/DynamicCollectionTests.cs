using System.Collections;
using System.Reflection;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class DynamicCollectionTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        namespace Demo;
        public static class EvaluationLog { public static string Order = ""; }
        public class View
        {
            private readonly Items _items = new();
            public int Reads { get; private set; }
            public Items Items { get { Reads++; return _items; } }
            public Entries Entries { get; } = new();
        }
        public class Items
        {
            public List<string> Values { get; } = new();
            public void Add(int value) => Values.Add("int:" + value);
            public void Add(string value) => Values.Add("string:" + value);
            public void Add(object value) => Values.Add("object:" + (value?.ToString() ?? "null"));
        }
        public class Entries
        {
            public List<string> Values { get; } = new();
            public void Add(int key, int value) => Values.Add(key + ":int:" + value);
            public void Add(int key, string value) => Values.Add(key + ":string:" + (value ?? "null"));
        }
        public class NullableItems
        {
            public string Selected { get; private set; }
            public void Add(int? value) => Selected = "int:" + (value?.ToString() ?? "null");
            public void Add(long? value) => Selected = "long:" + (value?.ToString() ?? "null");
        }
        public class NumericItems
        {
            public void Add(int value) { }
            public void Add(long value) { }
        }
        public interface IIntegers { void Add(int value); }
        public interface IStrings { void Add(string value); }
        public class InterfaceItems : IIntegers, IStrings
        {
            public string Selected { get; private set; }
            void IIntegers.Add(int value) => Selected = "integer";
            void IStrings.Add(string value) => Selected = "text";
        }
        public class IncompatibleEntries
        {
            public void Add(int key, int value) { }
            public void Add(string key, string value) { }
        }
        public class KeyExtension
        {
            public static int Calls;
            public int ProvideValue() { Calls++; EvaluationLog.Order += "key;"; return 21; }
        }
        public class ItemExtension
        {
            public static int Calls;
            public string Kind { get; set; }
            public object ProvideValue()
            {
                Calls++;
                EvaluationLog.Order += "value;";
                return Kind switch { "integer" => 21, "text" => "hello", "boolean" => true, _ => null };
            }
        }
        """;
    private const string Prefix = "<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>";

    [Theory]
    [InlineData("integer", "int:21")]
    [InlineData("text", "string:hello")]
    [InlineData("boolean", "object:True")]
    [InlineData("null", "object:null")]
    public void SelectsCollectionAdderFromTheRuntimeValue(string kind, string expected)
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Items><ItemExtension Kind='" + kind + "'/></View.Items></View>", Model);
        var root = code.Build();
        Assert.Equal(1, root.GetType().GetProperty("Reads")!.GetValue(root));
        Assert.Equal(expected, Assert.Single(Values(root, "Items")));
        Assert.Equal(1, code.Assembly.GetType("Demo.ItemExtension")!.GetField("Calls")!.GetValue(null));
    }

    [Theory]
    [InlineData("integer", "21:int:21")]
    [InlineData("text", "21:string:hello")]
    [InlineData("null", "21:string:null")]
    public void KeyedAddersConvertTheKeyOnceAndDispatchTheValue(string kind, string expected)
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Entries><ItemExtension x:Key='21' Kind='" + kind + "'/></View.Entries></View>", Model);
        var root = code.Build();
        Assert.Equal(expected, Assert.Single(Values(root, "Entries")));
        Assert.Equal(1, code.Assembly.GetType("Demo.ItemExtension")!.GetField("Calls")!.GetValue(null));
    }

    [Fact]
    public void UnmatchedRuntimeValuesThrowInvalidCast()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Entries><ItemExtension x:Key='21' Kind='boolean'/></View.Entries></View>", Model);
        Assert.IsType<InvalidCastException>(Assert.Throws<TargetInvocationException>(() => code.Build()).InnerException);
        Assert.Equal(1, code.Assembly.GetType("Demo.ItemExtension")!.GetField("Calls")!.GetValue(null));
    }

    [Fact]
    public void AttributeMarkupUsesTheSameRuntimeDispatch()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' Items='{Item Kind=text}'/>", Model);
        Assert.Equal("string:hello", Assert.Single(Values(code.Build(), "Items")));
    }

    [Fact]
    public void MarkupKeysAreEvaluatedOnceBeforeTheProvidedValue()
    {
        using var code = CompiledXaml.Create(Prefix + "<View.Entries><ItemExtension x:Key='{Key}' Kind='text'/></View.Entries></View>", Model);
        Assert.Equal("21:string:hello", Assert.Single(Values(code.Build(), "Entries")));
        Assert.Equal(1, code.Assembly.GetType("Demo.KeyExtension")!.GetField("Calls")!.GetValue(null));
        Assert.Equal(1, code.Assembly.GetType("Demo.ItemExtension")!.GetField("Calls")!.GetValue(null));
        Assert.Equal("key;value;", code.Assembly.GetType("Demo.EvaluationLog")!.GetField("Order")!.GetValue(null));
    }

    [Theory]
    [InlineData("integer", "int:21")]
    [InlineData("null", "int:null")]
    public void NullableAddersMatchBoxedValuesAndNull(string kind, string expected)
    {
        using var code = CompiledXaml.Create("<NullableItems xmlns='clr-namespace:Demo'><ItemExtension Kind='" + kind + "'/></NullableItems>", Model);
        var root = code.Build();
        Assert.Equal(expected, root.GetType().GetProperty("Selected")!.GetValue(root));
    }

    [Fact]
    public void NullWithoutANullableAdderThrowsNullReference()
    {
        using var code = CompiledXaml.Create("<NumericItems xmlns='clr-namespace:Demo'><ItemExtension Kind='null'/></NumericItems>", Model);
        Assert.IsType<NullReferenceException>(Assert.Throws<TargetInvocationException>(() => code.Build()).InnerException);
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("text")]
    public void DispatchesThroughExplicitInterfaceAdders(string kind)
    {
        using var code = CompiledXaml.Create("<InterfaceItems xmlns='clr-namespace:Demo'><ItemExtension Kind='" + kind + "'/></InterfaceItems>", Model);
        var root = code.Build();
        Assert.Equal(kind, root.GetType().GetProperty("Selected")!.GetValue(root));
    }

    [Fact]
    public void IncompatibleKeyOverloadsHaveSourceDiagnostics()
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("""
            <IncompatibleEntries xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ItemExtension x:Key="21" Kind="text"/>
            </IncompatibleEntries>
            """), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1016" && diagnostic.Message.Contains("key types"));
    }

    private static string[] Values(object root, string property)
    {
        var collection = root.GetType().GetProperty(property)!.GetValue(root)!;
        return ((IEnumerable)collection.GetType().GetProperty("Values")!.GetValue(collection)!).Cast<string>().ToArray();
    }
}
