#define BUILDTASK
using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using XamlG.Internal;

namespace XamlG.Frameworks.Avalonia.Parsing
{
#if !BUILDTASK
    public
#endif
    static class SpanHelpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseUInt(this ReadOnlySpan<char> span, NumberStyles style, IFormatProvider provider, out uint value)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.TryParseUInt(span, style, provider, out value);
#else
            return uint.TryParse(span, style, provider, out value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseInt(this ReadOnlySpan<char> span, out int value)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.TryParseInt(span, NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
#else
            return int.TryParse(span, out value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseInt(this ReadOnlySpan<char> span, NumberStyles style, IFormatProvider provider, out int value)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.TryParseInt(span, style, provider, out value);
#else
            return int.TryParse(span, style, provider, out value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseDouble(this ReadOnlySpan<char> span, NumberStyles style, IFormatProvider provider, out double value)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.TryParseDouble(span, style, provider, out value);
#else
            return double.TryParse(span, style, provider, out value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ParseDouble(this ReadOnlySpan<char> span, IFormatProvider provider)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.ParseDouble(span, provider);
#else
            return double.Parse(span, provider: provider);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseByte(this ReadOnlySpan<char> span, NumberStyles style, IFormatProvider provider, out byte value)
        {
#if NETSTANDARD2_0
            return SpanNumberParser.TryParseByte(span, style, provider, out value);
#else
            return byte.TryParse(span, style, provider, out value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryParseEnum<TEnum>(this ReadOnlySpan<char> span, bool ignoreCase, out TEnum value) where TEnum : struct
        {
#if NETSTANDARD2_0
            return SpanEnumParser.TryParse(span, ignoreCase, out value);
#else
            return Enum.TryParse<TEnum>(span, ignoreCase, out value);
#endif
        }
    }
}
