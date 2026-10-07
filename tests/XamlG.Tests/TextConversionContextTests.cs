using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class TextConversionContextTests
{
    private const string Model = """
        using System;
        using System.ComponentModel;
        using System.Globalization;
        namespace Demo;
        public class View
        {
            [TypeConverter(typeof(NegativeConverter))] public int Number { get; set; }
            public Type Type { get; set; }
            public Argument Argument { get; set; }
            public Parsed Parsed { get; set; }
            [TypeConverter(typeof(ParsedConverter))] public Parsed Converted { get; set; }
        }
        public class Argument(Type type) { public Type Type { get; } = type; }
        public class ConvertedNumberExtension([TypeConverter(typeof(NegativeConverter))] int value)
        {
            public int ProvideTypedValue() => value;
        }
        public static class Attached
        {
            [TypeConverter(typeof(NegativeConverter))] public static int GetNumber(View view) => view.Number;
            public static void SetNumber(View view, int value) => view.Number = value;
        }
        public class NegativeConverter : TypeConverter
        {
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                => -int.Parse((string)value, culture);
        }
        [TypeConverter(typeof(ParsedConverter))]
        public class Parsed
        {
            public string Value { get; set; }
            public static Parsed Parse(string value, IFormatProvider provider) => new() { Value = "parse:" + value };
        }
        public class ParsedConverter : TypeConverter
        {
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                => new Parsed { Value = "converter:" + value };
        }
        """;

    [Theory]
    [InlineData("Number='21'", "")]
    [InlineData("", "<View.Number>21</View.Number>")]
    [InlineData("", "<View.Number><x:String>21</x:String></View.Number>")]
    [InlineData("Attached.Number='21'", "")]
    [InlineData("", "<Attached.Number>21</Attached.Number>")]
    [InlineData("", "<Attached.Number><x:String>21</x:String></Attached.Number>")]
    public void PreservesMemberConvertersAcrossAttributeAndElementForms(string attributes, string content)
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + attributes + ">" + content + "</View>", Model);
        var root = code.Build();
        Assert.Equal(-21, root.GetType().GetProperty("Number")!.GetValue(root));
    }

    [Fact]
    public void ConvertsStringElementsInTheirOwnNamespaceScope()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Type><x:String xmlns:local="clr-namespace:Demo">local:View</x:String></View.Type>
            </View>
            """, Model);
        var root = code.Build();
        Assert.Equal(root.GetType(), root.GetType().GetProperty("Type")!.GetValue(root));
    }

    [Fact]
    public void ConstructorArgumentsInheritDirectiveNamespaceDeclarations()
    {
        using var code = CompiledXaml.Create("""
            <View xmlns="clr-namespace:Demo" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <View.Argument><Argument>
                <x:Arguments xmlns:local="clr-namespace:Demo"><x:String>local:View</x:String></x:Arguments>
              </Argument></View.Argument>
            </View>
            """, Model);
        var root = code.Build();
        var argument = root.GetType().GetProperty("Argument")!.GetValue(root)!;
        Assert.Equal(root.GetType(), argument.GetType().GetProperty("Type")!.GetValue(argument));
    }

    [Theory]
    [InlineData("<View xmlns='clr-namespace:Demo' Number='{ConvertedNumber 21}'/>")]
    [InlineData("<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><View.Number><ConvertedNumberExtension><x:Arguments><x:String>21</x:String></x:Arguments></ConvertedNumberExtension></View.Number></View>")]
    public void PreservesConstructorParameterConversionMetadata(string xaml)
    {
        using var code = CompiledXaml.Create(xaml, Model);
        var root = code.Build();
        Assert.Equal(-21, root.GetType().GetProperty("Number")!.GetValue(root));
    }

    [Fact]
    public void ParseTakesPrecedenceOverTypeConvertersButNotPropertyConverters()
    {
        using var code = CompiledXaml.Create("<View xmlns='clr-namespace:Demo' Parsed='value' Converted='value'/>", Model);
        var root = code.Build();
        string Value(string property)
        {
            var value = root.GetType().GetProperty(property)!.GetValue(root)!;
            return (string)value.GetType().GetProperty("Value")!.GetValue(value)!;
        }
        Assert.Equal("parse:value", Value("Parsed"));
        Assert.Equal("converter:value", Value("Converted"));
    }
}
