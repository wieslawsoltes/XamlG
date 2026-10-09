using System.Globalization;
using System.Reflection;
using XamlG.Frameworks;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SpanNumberParserTests
{
    private delegate bool TryNumber<T>(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider provider, out T value);
    private delegate double ParseNumber(ReadOnlySpan<char> text, IFormatProvider provider);
    private static readonly Type Parser = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Internal.SpanNumberParser", throwOnError: true)!;
    private static readonly TryNumber<int> Int = Bind<TryNumber<int>>("TryParseInt");
    private static readonly TryNumber<uint> UInt = Bind<TryNumber<uint>>("TryParseUInt");
    private static readonly TryNumber<byte> Byte = Bind<TryNumber<byte>>("TryParseByte");
    private static readonly TryNumber<double> Double = Bind<TryNumber<double>>("TryParseDouble");
    private static readonly TryNumber<float> Float = Bind<TryNumber<float>>("TryParseFloat");
    private static readonly ParseNumber Parse = Bind<ParseNumber>("ParseDouble");

    private static T Bind<T>(string name) where T : Delegate =>
        (T)Parser.GetMethod(name, typeof(T).GetMethod("Invoke")!.GetParameters().Select(value => value.ParameterType).ToArray())!.CreateDelegate(typeof(T));

    public static IEnumerable<object[]> Numbers()
    {
        string[] inputs = ["", "0", "-0", "+0", "12", "-12", "  12  ", "12\0", "12\0\0", "12\0x", "0x10", "FF",
            "255", "256", "2147483647", "2147483648", "-2147483648", "-2147483649", "4294967295", "4294967296",
            "1.25", "1,25", "1,234.5", "1\u202f234,5", "1e-308", "5e-324", "1e309", "-1e309",
            "1.7976931348623157e308", "2.2250738585072014e-308", "9007199254740993", "0.100000000000000005",
            "NaN", "Infinity", "-Infinity", "∞", "-∞", "1e", "--1", "1 2", "１２", "\u00a01\u00a0"];
        foreach (var culture in new[] { "", "en-US", "fr-FR" })
            foreach (var input in inputs) yield return [input, culture];
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void NumericSpansRetainHostBclGrammarCultureOverflowAndRounding(string text, string culture)
    {
        var provider = CultureInfo.GetCultureInfo(culture);
        // A slice must be parsed without depending on a complete backing string.
        var storage = "prefix" + text + "suffix";
        var span = storage.AsSpan(6, text.Length);
        foreach (var style in new[] { NumberStyles.Integer, NumberStyles.HexNumber, NumberStyles.Any })
        {
            var intSuccess = int.TryParse(text, style, provider, out var expectedInt);
            Assert.Equal(intSuccess, Int(span, style, provider, out var actualInt));
            Assert.Equal(expectedInt, actualInt);
            var uintSuccess = uint.TryParse(text, style, provider, out var expectedUInt);
            Assert.Equal(uintSuccess, UInt(span, style, provider, out var actualUInt));
            Assert.Equal(expectedUInt, actualUInt);
            var byteSuccess = byte.TryParse(text, style, provider, out var expectedByte);
            Assert.Equal(byteSuccess, Byte(span, style, provider, out var actualByte));
            Assert.Equal(expectedByte, actualByte);
        }
        foreach (var style in new[] { NumberStyles.Float, NumberStyles.Float | NumberStyles.AllowThousands, NumberStyles.Any })
        {
            var success = double.TryParse(text, style, provider, out var expected);
            Assert.Equal(success, Double(span, style, provider, out var actual));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
            var floatSuccess = float.TryParse(text, style, provider, out var expectedFloat);
            Assert.Equal(floatSuccess, Float(span, style, provider, out var actualFloat));
            Assert.Equal(BitConverter.SingleToInt32Bits(expectedFloat), BitConverter.SingleToInt32Bits(actualFloat));
        }
        if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, provider, out var parsed))
            Assert.Equal(BitConverter.DoubleToInt64Bits(parsed), BitConverter.DoubleToInt64Bits(Parse(span, provider)));
        else
            Assert.Throws<FormatException>(() => Parse(text.AsSpan(), provider));
    }

    [Fact]
    public void InvalidNumericStylesKeepTheirArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Double("12".AsSpan(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
        Assert.Throws<ArgumentException>(() => Int("12".AsSpan(), (NumberStyles)(-1), CultureInfo.InvariantCulture, out _));
    }

    [Fact]
    public void ModernHostParsesNumericTokensWithoutAllocatingStrings()
    {
        var provider = CultureInfo.InvariantCulture;
        for (var index = 0; index < 1000; index++) Read(provider);
        var before = GC.GetAllocatedBytesForCurrentThread();
        double sum = 0;
        for (var index = 0; index < 4096; index++) sum += Read(provider);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4096 * (12.5 + 12 + 255 + 12345 + 12.5), sum);
        Assert.Equal(0, allocated);
    }

    private static double Read(IFormatProvider provider)
    {
        if (!Double("12.5".AsSpan(), NumberStyles.Float, provider, out var d) ||
            !Int("12".AsSpan(), NumberStyles.Integer, provider, out var i) ||
            !Byte("ff".AsSpan(), NumberStyles.HexNumber, provider, out var b) ||
            !UInt("12345".AsSpan(), NumberStyles.Integer, provider, out var u))
            throw new InvalidOperationException("The numeric span parser rejected a valid token.");
        return d + i + b + u + Parse("12.5".AsSpan(), provider);
    }
}
