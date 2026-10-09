using System;
using System.Collections.Generic;
using System.Globalization;
using XamlG.Internal;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    /// <summary>
    /// The <see cref="UnicodeRange"/> descripes a set of Unicode characters.
    /// </summary>
    internal readonly record struct UnicodeRange
    {
        public readonly static UnicodeRange Default = Parse("0-10FFFD");

        private readonly UnicodeRangeSegment _single;
        private readonly IReadOnlyList<UnicodeRangeSegment>? _segments = null;

        public UnicodeRange(int start, int end)
        {
            _single = new UnicodeRangeSegment(start, end);
        }

        public UnicodeRange(UnicodeRangeSegment single)
        {
            _single = single;
        }

        public UnicodeRange(IReadOnlyList<UnicodeRangeSegment> segments)
        {
            if(segments is null || segments.Count == 0)
            {
                throw new ArgumentException(nameof(segments));
            }

            _single = segments[0];
            _segments = segments;
        }

        internal UnicodeRangeSegment Single => _single;

        internal IReadOnlyList<UnicodeRangeSegment>? Segments => _segments;

        /// <summary>
        /// Determines if given value is inside the range.
        /// </summary>
        /// <param name="value">The value to verify.</param>
        /// <returns>
        /// <c>true</c> If given value is inside the range, <c>false</c> otherwise.
        /// </returns>
        public bool IsInRange(int value)
        {
            if(_segments is null)
            {
                return _single.IsInRange(value);
            }

            foreach(var segment in _segments)
            {
                if (segment.IsInRange(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Parses a <see cref="UnicodeRange"/>.
        /// </summary>
        /// <param name="s">The string to parse.</param>
        /// <returns>The parsed <see cref="UnicodeRange"/>.</returns>
        /// <exception cref="FormatException"></exception>
        public static UnicodeRange Parse(string s) => Parse(s.AsSpan());

        public static UnicodeRange Parse(ReadOnlySpan<char> s)
        {
            if (s.IsEmpty) throw new FormatException("Could not parse specified Unicode range.");
            var count = 1;
            foreach (var c in s) if (c == ',') count++;
            // Upstream trims segments only for the multiple-segment form.
            if (count == 1) return new UnicodeRange(UnicodeRangeSegment.Parse(s));
            var segments = new UnicodeRangeSegment[count];
            var index = 0;
            foreach (var part in new SpanSplitEnumerator(s, ','))
                segments[index++] = UnicodeRangeSegment.Parse(part.Trim());
            return new UnicodeRange(segments);
        }
    }

    internal readonly record struct UnicodeRangeSegment
    {
        public UnicodeRangeSegment(int start, int end)
        {
            Start = start;
            End = end;
        }

        /// <summary>
        /// Get the start of the segment.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Get the end of the segment.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Determines if given value is inside the range segment.
        /// </summary>
        /// <param name="value">The value to verify.</param>
        /// <returns>
        /// <c>true</c> If given value is inside the range segment, <c>false</c> otherwise.
        /// </returns>
        public bool IsInRange(int value)
        {
            return Start <= value && value <= End;
        }

        /// <summary>
        /// Parses a <see cref="UnicodeRangeSegment"/>.
        /// </summary>
        /// <param name="s">The string to parse.</param>
        /// <returns>The parsed <see cref="UnicodeRangeSegment"/>.</returns>
        /// <exception cref="FormatException"></exception>
        public static UnicodeRangeSegment Parse(string s) => Parse(s.AsSpan());

        public static UnicodeRangeSegment Parse(ReadOnlySpan<char> s)
        {
            var separator = s.IndexOf('-');
            if (separator < 0)
            {
                if (!TryParseToken(s, out var start, out var end)) throw Invalid();
                return new UnicodeRangeSegment(start, end);
            }
            // Validate both tokens before parsing their values, as the regex version does.
            var first = s.Slice(0, separator);
            var second = s.Slice(separator + 1);
            if (!TryParseToken(first, out var firstStart, out _) ||
                !TryParseToken(second, out var secondStart, out _)) throw Invalid();
            if (first.IndexOf('?') >= 0) ThrowWildcardNumber(first);
            if (second.IndexOf('?') >= 0) ThrowWildcardNumber(second);
            return new UnicodeRangeSegment(firstStart, secondStart);
        }

        private static bool TryParseToken(ReadOnlySpan<char> token, out int start, out int end)
        {
            start = end = 0;
            // The original regex's '$' accepts exactly one final LF.
            if (!token.IsEmpty && token[token.Length - 1] == '\n') token = token.Slice(0, token.Length - 1);
            if (token.Length >= 2 && (token[0] == 'u' || token[0] == 'U') && token[1] == '+') token = token.Slice(2);
            if (token.IsEmpty || token.Length > 6) return false;
            for (var i = 0; i < token.Length; i++)
            {
                var c = token[i];
                var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 :
                    c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0 && !(i > 0 && c == '?')) return false;
                start = (start << 4) | (digit < 0 ? 0 : digit);
                end = (end << 4) | (digit < 0 ? 15 : digit);
            }
            return true;
        }

        private static void ThrowWildcardNumber(ReadOnlySpan<char> token)
        {
            if (token[token.Length - 1] == '\n') token = token.Slice(0, token.Length - 1);
            if (token.Length >= 2 && token[1] == '+') token = token.Slice(2);
            // Retain the host's numeric exception text for invalid wildcard endpoints.
            _ = int.Parse(token.ToString(), NumberStyles.HexNumber);
        }

        private static FormatException Invalid() => new FormatException("Could not parse specified Unicode range segment.");
    }
}
