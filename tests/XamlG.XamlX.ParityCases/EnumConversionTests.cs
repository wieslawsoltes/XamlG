using System;
using System.ComponentModel;
using System.Globalization;
using System.Security;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class EnumConversionTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("Plain", "First", "1")]
    [InlineData("Plain", "High", "-2147483648")]
    [InlineData("Nullable", "Second", "2")]
    [InlineData("Flags", "First", "1")]
    [InlineData("Flags", " First ", "1")]
    [InlineData("Flags", "First, Second", "3")]
    [InlineData("Flags", "First,Second,Third", "7")]
    [InlineData("Flags", "First,First", "1")]
    [InlineData("Flags", "First, All", "-1")]
    [InlineData("Byte", "High", "255")]
    [InlineData("SByte", "High", "-128")]
    [InlineData("Short", "High", "-32768")]
    [InlineData("UShort", "High", "65535")]
    [InlineData("UInt", "High", "4294967295")]
    [InlineData("Long", "High", "-9223372036854775808")]
    [InlineData("ULong", "High", "18446744073709551615")]
    [InlineData("ByteFlags", "First,High", "129")]
    [InlineData("UIntFlags", "First,High", "2147483649")]
    [InlineData("ULongFlags", "First,High", "9223372036854775809")]
    public void NamesPreserveDeclaredValuesAndFlags(string property, string literal, string expected) =>
        Assert.Equal(expected, Read((EnumLiteralHost)CompileAndRun(Attribute(property, literal)), property));

    [Theory]
    [InlineData("Plain", "first")]
    [InlineData("Plain", "FIRST")]
    [InlineData("Plain", " First ")]
    [InlineData("Plain", "First,Second")]
    [InlineData("Plain", "Unknown")]
    [InlineData("Nullable", "first")]
    [InlineData("Flags", "first")]
    [InlineData("Flags", "First,second")]
    [InlineData("Flags", "First,")]
    [InlineData("Flags", ",First")]
    [InlineData("Flags", "First,,Second")]
    [InlineData("Flags", "First,2")]
    [InlineData("Flags", "1,2")]
    [InlineData("Plain", "0x10")]
    [InlineData("Plain", "1.0")]
    [InlineData("Plain", "1e2")]
    [InlineData("Plain", "9223372036854775808")]
    [InlineData("Long", "-9223372036854775809")]
    [InlineData("ULong", "18446744073709551615")]
    public void UnsupportedNamesAndNumbersFailCompilation(string property, string literal) =>
        Assert.ThrowsAny<XamlParseException>(() => Compile(Attribute(property, literal)));

    [Theory]
    [InlineData("Plain", " +42 ", "42")]
    [InlineData("Plain", "-0", "0")]
    [InlineData("Plain", "2147483648", "-2147483648")]
    [InlineData("Plain", "4294967295", "-1")]
    [InlineData("Plain", "4294967296", "0")]
    [InlineData("Plain", "-2147483649", "2147483647")]
    [InlineData("Plain", "9223372036854775807", "-1")]
    [InlineData("Plain", "-9223372036854775808", "0")]
    [InlineData("Nullable", "4294967295", "-1")]
    [InlineData("Flags", "7", "7")]
    [InlineData("UInt", "4294967295", "4294967295")]
    [InlineData("UInt", "-1", "4294967295")]
    [InlineData("UInt", "4294967296", "0")]
    [InlineData("UInt", "-4294967297", "4294967295")]
    [InlineData("UInt", "-9223372036854775808", "0")]
    [InlineData("Long", "9223372036854775807", "9223372036854775807")]
    [InlineData("Long", "-9223372036854775808", "-9223372036854775808")]
    [InlineData("ULong", "9223372036854775807", "9223372036854775807")]
    [InlineData("ULong", "-1", "18446744073709551615")]
    [InlineData("ULong", "-9223372036854775808", "9223372036854775808")]
    [InlineData("Byte", "255", "255")]
    [InlineData("Byte", "256", "0")]
    [InlineData("Byte", "-1", "255")]
    [InlineData("SByte", "128", "-128")]
    [InlineData("Short", "32768", "-32768")]
    [InlineData("UShort", "-1", "65535")]
    public void NumericLiteralsRetainTheUnderlyingBits(string property, string literal, string expected) =>
        Assert.Equal(expected, Read((EnumLiteralHost)CompileAndRun(Attribute(property, literal)), property));

    [Theory]
    [InlineData("Plain", "Second", "2")]
    [InlineData("Flags", "First,Second", "3")]
    [InlineData("ULong", "-1", "18446744073709551615")]
    public void PropertyTextUsesTheSameEnumConversion(string property, string literal, string expected)
    {
        var xaml = "<EnumLiteralHost" + Namespace + "><EnumLiteralHost." + property + ">" + literal + "</EnumLiteralHost." + property + "></EnumLiteralHost>";
        Assert.Equal(expected, Read((EnumLiteralHost)CompileAndRun(xaml), property));
    }

    [Theory]
    [InlineData("Converted", "Known", "1")]
    [InlineData("Converted", "2", "2")]
    [InlineData("Converted", "alias", "7")]
    [InlineData("Converted", " Known ", "7")]
    [InlineData("Converted", "9223372036854775808", "7")]
    [InlineData("Override", "First", "2")]
    [InlineData("Override", "1", "2")]
    [InlineData("Override", "alias", "2")]
    public void ConvertersKeepTheirPrecedenceAndFallback(string property, string literal, string expected) =>
        Assert.Equal(expected, Read((EnumLiteralHost)CompileAndRun(Attribute(property, literal)), property));

    [Fact]
    public void EnumConstructorArgumentsUseTheSameNumericConversion()
    {
        var root = (EnumArgumentHost)CompileAndRun("<EnumArgumentHost" + Namespace + "><x:Arguments><x:String>4294967295</x:String></x:Arguments></EnumArgumentHost>");
        Assert.Equal((LiteralInt)(-1), root.Value);
    }

    private static string Attribute(string property, string literal) =>
        "<EnumLiteralHost" + Namespace + " " + property + "='" + SecurityElement.Escape(literal) + "'/>";

    private static string Read(EnumLiteralHost root, string property)
    {
        var value = typeof(EnumLiteralHost).GetProperty(property)!.GetValue(root)!;
        return Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)!;
    }
}

public sealed class EnumLiteralHost
{
    public LiteralInt Plain { get; set; }
    public LiteralInt? Nullable { get; set; }
    public LiteralFlags Flags { get; set; }
    public LiteralByte Byte { get; set; }
    public LiteralSByte SByte { get; set; }
    public LiteralShort Short { get; set; }
    public LiteralUShort UShort { get; set; }
    public LiteralUInt UInt { get; set; }
    public LiteralLong Long { get; set; }
    public LiteralULong ULong { get; set; }
    public LiteralByteFlags ByteFlags { get; set; }
    public LiteralUIntFlags UIntFlags { get; set; }
    public LiteralULongFlags ULongFlags { get; set; }
    public ConvertedLiteralEnum Converted { get; set; }
    [TypeConverter(typeof(LiteralEnumPropertyConverter))]
    public LiteralInt Override { get; set; }
}

public sealed class EnumArgumentHost
{
    public EnumArgumentHost(LiteralInt value) => Value = value;
    public LiteralInt Value { get; }
}

public enum LiteralInt { First = 1, Second = 2, High = int.MinValue }
[Flags]
public enum LiteralFlags { First = 1, Second = 2, Third = 4, All = -1 }
public enum LiteralByte : byte { High = byte.MaxValue }
public enum LiteralSByte : sbyte { High = sbyte.MinValue }
public enum LiteralShort : short { High = short.MinValue }
public enum LiteralUShort : ushort { High = ushort.MaxValue }
public enum LiteralUInt : uint { High = uint.MaxValue }
public enum LiteralLong : long { High = long.MinValue }
public enum LiteralULong : ulong { High = ulong.MaxValue }
[Flags]
public enum LiteralByteFlags : byte { First = 1, High = 128 }
[Flags]
public enum LiteralUIntFlags : uint { First = 1, High = 0x80000000U }
[Flags]
public enum LiteralULongFlags : ulong { First = 1, High = 0x8000000000000000UL }

[TypeConverter(typeof(LiteralEnumTypeConverter))]
public enum ConvertedLiteralEnum { Known = 1, Alias = 7 }

public sealed class LiteralEnumTypeConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => ConvertedLiteralEnum.Alias;
}

public sealed class LiteralEnumPropertyConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => LiteralInt.Second;
}
