using System;
using System.Globalization;
using System.Reflection;

namespace XamlG.Internal;

// netstandard2.0 exposes spans through System.Memory, but its reference
// assemblies do not expose the BCL's span-based numeric parsers. Bind those
// exact APIs once when the compiler host provides them. Older hosts retain
// their own string parser, including its grammar, culture and rounding.
internal static class SpanNumberParser
{
    private delegate bool TryNumber<T>(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out T value);
    private delegate double ParseNumber(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider);
    private delegate ulong ParseUnsigned(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider);

    private static readonly TryNumber<int> Int = Bind<TryNumber<int>>(
        typeof(int).GetMethod(nameof(int.TryParse), Signature(typeof(int))),
        static (ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out int value) =>
            int.TryParse(text.ToString(), style, provider, out value));

    private static readonly TryNumber<uint> UInt = Bind<TryNumber<uint>>(
        typeof(uint).GetMethod(nameof(uint.TryParse), Signature(typeof(uint))),
        static (ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out uint value) =>
            uint.TryParse(text.ToString(), style, provider, out value));

    private static readonly TryNumber<byte> Byte = Bind<TryNumber<byte>>(
        typeof(byte).GetMethod(nameof(byte.TryParse), Signature(typeof(byte))),
        static (ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out byte value) =>
            byte.TryParse(text.ToString(), style, provider, out value));

    private static readonly TryNumber<double> Double = Bind<TryNumber<double>>(
        typeof(double).GetMethod(nameof(double.TryParse), Signature(typeof(double))),
        static (ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out double value) =>
            double.TryParse(text.ToString(), style, provider, out value));

    private static readonly TryNumber<float> Float = Bind<TryNumber<float>>(
        typeof(float).GetMethod(nameof(float.TryParse), Signature(typeof(float))),
        static (ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out float value) =>
            float.TryParse(text.ToString(), style, provider, out value));

    private static readonly ParseNumber Parse = Bind<ParseNumber>(
        typeof(double).GetMethod(nameof(double.Parse),
            new[] { typeof(ReadOnlySpan<char>), typeof(NumberStyles), typeof(IFormatProvider) }),
        static (text, style, provider) => double.Parse(text.ToString(), style, provider));

    private static readonly ParseUnsigned Unsigned = Bind<ParseUnsigned>(
        typeof(ulong).GetMethod(nameof(ulong.Parse),
            new[] { typeof(ReadOnlySpan<char>), typeof(NumberStyles), typeof(IFormatProvider) }),
        static (text, style, provider) => ulong.Parse(text.ToString(), style, provider));

    private static Type[] Signature(Type result) =>
        new[] { typeof(ReadOnlySpan<char>), typeof(NumberStyles), typeof(IFormatProvider), result.MakeByRefType() };

    private static T Bind<T>(MethodInfo? method, T fallback) where T : Delegate =>
        method == null ? fallback : (T)Delegate.CreateDelegate(typeof(T), method);

    public static bool TryParseInt(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out int value) =>
        Int(text, style, provider, out value);

    public static bool TryParseUInt(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out uint value) =>
        UInt(text, style, provider, out value);

    public static bool TryParseByte(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out byte value) =>
        Byte(text, style, provider, out value);

    public static bool TryParseDouble(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out double value) =>
        Double(text, style, provider, out value);

    public static bool TryParseFloat(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider, out float value) =>
        Float(text, style, provider, out value);

    public static double ParseDouble(ReadOnlySpan<char> text, IFormatProvider? provider) =>
        Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, provider);

    public static double ParseDouble(ReadOnlySpan<char> text, NumberStyles style, IFormatProvider? provider) =>
        Parse(text, style, provider);

    public static ulong ParseUInt64(ReadOnlySpan<char> text, IFormatProvider? provider) =>
        Unsigned(text, NumberStyles.Integer, provider);
}
