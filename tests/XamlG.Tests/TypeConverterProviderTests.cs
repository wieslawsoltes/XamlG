using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using Xunit;

namespace XamlG.Tests;

public sealed class TypeConverterProviderTests
{
    private const string Model = """
        using System.ComponentModel;
        using System.Globalization;
        namespace Demo;
        public class View
        {
            public Token Value { get; set; }
            [TypeConverter(typeof(MemberConverter))] public Token Override { get; set; }
            public ParsedToken Parsed { get; set; }
        }
        [TypeConverter(typeof(DeclaredConverter))]
        public class Token { public string Text { get; set; } }
        public class ParsedToken : Token
        {
            public static ParsedToken Parse(string text) => new() { Text = "parse:" + text };
        }
        public class MappedConverter : TypeConverter
        {
            public static int Calls { get; private set; }
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
            { Calls++; return new Token { Text = "mapped:" + value }; }
        }
        public class DeclaredConverter : TypeConverter
        {
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                => new Token { Text = "declared:" + value };
        }
        public class MemberConverter : TypeConverter
        {
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                => new Token { Text = "member:" + value };
        }
        public class NoDefaultConstructor : TypeConverter { public NoDefaultConstructor(string argument) { } }
        public class NotAConverter { }
        public static class RuntimeText { public static string Value => "input"; }
        """;

    [Theory]
    [InlineData("Value", "input", "mapped:input")]
    [InlineData("Value", "{x:Static RuntimeText.Value}", "mapped:input")]
    [InlineData("Override", "input", "member:input")]
    [InlineData("Parsed", "input", "parse:input")]
    [InlineData("Parsed", "{x:Static RuntimeText.Value}", "parse:input")]
    public void ProvidersKeepMemberParserAndDeclaredConverterPrecedence(string property, string text, string expected)
    {
        using var code = Compile(property, text, "Demo.MappedConverter");
        var calls = code.Assembly.GetType("Demo.MappedConverter")!.GetProperty("Calls")!;
        Assert.Equal(0, calls.GetValue(null));
        var root = code.Build();
        var value = root.GetType().GetProperty(property)!.GetValue(root)!;
        Assert.Equal(expected, value.GetType().GetProperty("Text")!.GetValue(value));
        Assert.Equal(expected.StartsWith("mapped:", StringComparison.Ordinal) ? 1 : 0, calls.GetValue(null));
    }

    [Theory]
    [InlineData("Demo.NoDefaultConstructor")]
    [InlineData("Demo.NotAConverter")]
    public void InvalidProviderResultsDoNotHideDeclaredConverters(string converter)
    {
        using var code = Compile("Value", "input", converter);
        var root = code.Build();
        var value = root.GetType().GetProperty("Value")!.GetValue(root)!;
        Assert.Equal("declared:input", value.GetType().GetProperty("Text")!.GetValue(value));
    }

    private static CompiledXaml Compile(string property, string text, string converter) => CompiledXaml.Create(
        "<View xmlns='clr-namespace:Demo' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " + property + "='" + text + "'/>",
        Model, XamlFrameworkProfile.Portable with { TypeConverterProviders = ImmutableArray.Create<IXamlTypeConverterProvider>(new Provider(converter)) });

    private sealed class Provider(string converter) : IXamlTypeConverterProvider
    {
        public INamedTypeSymbol? GetConverter(BindingContext context, ITypeSymbol targetType) =>
            targetType.Name is "Token" or "ParsedToken" ? context.Types.Find(converter) : null;
    }
}
