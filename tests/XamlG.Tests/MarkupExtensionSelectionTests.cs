using System.Collections;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MarkupExtensionSelectionTests
{
    private const string Model = """
        using System;
        using System.Collections.Generic;
        namespace Demo;
        public class View
        {
            public string Text { get; set; }
            public List<string> Items { get; } = new();
            public Argument Value { get; set; }
        }
        public class Argument(string value) { public string Value { get; } = value; }
        public class TypedExtension { public string ProvideTypedValue() => "typed"; }
        public class PreferredExtension
        {
            public object ProvideValue() => "object";
            public string ProvideTypedValue() => "typed";
        }
        public class ParameterlessExtension
        {
            public object ProvideValue() => "parameterless";
            public string ProvideTypedValue(IServiceProvider services) => "service";
        }
        public class OverloadedExtension
        {
            public object ProvideValue() => "parameterless";
            public string ProvideValue(IServiceProvider services) => "service";
        }
        public class InheritedExtension : TypedExtension { }
        public class ServiceExtension
        {
            public object ProvideValue(IServiceProvider services) => "object";
            public string ProvideTypedValue(IServiceProvider services) => services == null ? "missing" : "typed";
        }
        public class InvalidExtension
        {
            public static string ProvideTypedValue() => "static";
            public string ProvideValue<T>() => "generic";
        }
        """;

    [Theory]
    [InlineData("Typed", "typed")]
    [InlineData("Preferred", "typed")]
    [InlineData("Parameterless", "parameterless")]
    [InlineData("Overloaded", "parameterless")]
    [InlineData("Inherited", "typed")]
    [InlineData("Service", "typed")]
    public void SelectsTheUpstreamProvideValueAlternative(string extension, string expected)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' Text='{" + extension + "}'/>", Model);
        var root = code.Build();
        Assert.Equal(expected, root.GetType().GetProperty("Text")!.GetValue(root));
    }

    [Fact]
    public void TypedExtensionElementsSupplyPropertiesCollectionsAndConstructorArguments()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Text><TypedExtension/></View.Text>
              <View.Items><TypedExtension/></View.Items>
              <View.Value><Argument><x:Arguments><TypedExtension/></x:Arguments></Argument></View.Value>
            </View>
            """, Model);
        var root = code.Build();
        Assert.Equal("typed", root.GetType().GetProperty("Text")!.GetValue(root));
        Assert.Equal("typed", Assert.Single(((IEnumerable)root.GetType().GetProperty("Items")!.GetValue(root)!).Cast<object>()));
        var argument = root.GetType().GetProperty("Value")!.GetValue(root)!;
        Assert.Equal("typed", argument.GetType().GetProperty("Value")!.GetValue(argument));
    }

    [Fact]
    public void RejectsStaticAndGenericProviderMethods()
    {
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<View xmlns='clr-namespace:Demo' Text='{Invalid}'/>"), CompilationFactory.Create(Model));
        Assert.False(document.Success);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XG1009");
    }
}
