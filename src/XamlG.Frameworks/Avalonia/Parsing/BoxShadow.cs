using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace XamlG.Frameworks.Avalonia.Parsing
{
    /// <summary>
    /// Represents a box shadow which can be attached to an element or control.
    /// </summary>
    internal struct BoxShadow
    {
        private readonly static char[] s_Separator = new char[] { ' ', '\t' };
        private const char OpeningParenthesis = '(';
        private const char ClosingParenthesis = ')';

        /// <summary>
        /// Gets or sets the horizontal offset (distance) of the shadow.
        /// </summary>
        /// <remarks>
        /// Positive values place the shadow to the right of the element while
        /// negative values place the shadow to the left.
        /// </remarks>
        public double OffsetX { get; set; }

        /// <summary>
        /// Gets or sets the vertical offset (distance) of the shadow.
        /// </summary>
        /// <remarks>
        /// Positive values place the shadow below the element while
        /// negative values place the shadow above.
        /// </remarks>
        public double OffsetY { get; set; }

        /// <summary>
        /// Gets or sets the blur radius.
        /// This is used to control the amount of blurring.
        /// </summary>
        /// <remarks>
        /// The larger this value, the bigger the blur effect, so the shadow becomes larger and more transparent.
        /// Negative values are not allowed. If not specified, the default (zero) is used and the shadow edge is sharp.
        /// </remarks>
        public double Blur { get; set; }

        /// <summary>
        /// Gets or sets the spread radius.
        /// This is used to control the overall size of the shadow.
        /// </summary>
        /// <remarks>
        /// Positive values will cause the shadow to expand and grow larger, negative values will cause the shadow to shrink.
        /// If not specified, the default (zero) is used and the shadow will be the same size as the element.
        /// </remarks>
        public double Spread { get; set; }

        /// <summary>
        /// Gets or sets the color of the shadow.
        /// </summary>
        public Color Color { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the shadow is inset and drawn within the element rather than outside of it.
        /// </summary>
        /// <remarks>
        /// Inset changes the shadow to inside the element (as if the content was depressed inside the box).
        /// If false (the default), the shadow is assumed to be a drop shadow (as if the box were raised above the content).
        /// <br/><br/>
        /// Inset shadows are drawn inside the element, above the background (even when it's transparent), but below any content.
        /// </remarks>
        public bool IsInset { get; set; }

        /// <summary>
        /// Indicates whether the current object is equal to another object of the same type.
        /// </summary>
        /// <param name="other">An object to compare with this object.</param>
        /// <returns>
        /// <c>true</c> if the current object is equal to the other parameter; otherwise, <c>false</c>.
        /// </returns>
        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator", Justification = "Bit equality is adequate here")]
        public bool Equals(in BoxShadow other)
        {
            return OffsetX == other.OffsetX
                && OffsetY == other.OffsetY
                && Blur == other.Blur
                && Spread == other.Spread
                && Color.Equals(other.Color)
                && IsInset == other.IsInset;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is BoxShadow other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = OffsetX.GetHashCode();
                hashCode = (hashCode * 397) ^ OffsetY.GetHashCode();
                hashCode = (hashCode * 397) ^ Blur.GetHashCode();
                hashCode = (hashCode * 397) ^ Spread.GetHashCode();
                hashCode = (hashCode * 397) ^ Color.GetHashCode();
                hashCode = (hashCode * 397) ^ IsInset.GetHashCode();
                return hashCode;
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            ToString(sb);
            return sb.ToString();
        }

        internal void ToString(StringBuilder sb)
        {
            if (this == default)
            {
                sb.Append("none");
                return;
            }

            if (IsInset)
            {
                sb.Append("inset ");
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} ", OffsetX);

            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} ", OffsetY);

            if (Blur != 0.0 || Spread != 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} ", Blur);
            }

            if (Spread != 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} ", Spread);
            }

            Color.ToString(sb);
        }

        /// <summary>
        /// Parses a <see cref="BoxShadow"/> string.
        /// </summary>
        /// <remarks>
        /// A box shadow may be specified in multiple formats with separate components:
        ///   <list type="bullet">
        ///     <item>Two, three, or four length values.</item>
        ///     <item>A color value.</item>
        ///     <item>An optional inset keyword.</item>
        ///   </list>
        /// If only two length values are given they will be interpreted as <see cref="OffsetX"/> and <see cref="OffsetY"/>.
        /// If a third value is given, it is interpreted as a <see cref="Blur"/>, and if a fourth value is given,
        /// it is interpreted as <see cref="Spread"/>.
        /// </remarks>
        /// <param name="s">The input string to parse.</param>
        /// <returns>A new <see cref="BoxShadow"/></returns>
        public static BoxShadow Parse(string s)
        {
            if (s == null) throw new ArgumentNullException();
            return Parse(s.AsSpan());
        }

        public static BoxShadow Parse(ReadOnlySpan<char> s)
        {
            if (s.IsEmpty) throw new FormatException();
            var parts = new BracketSplitEnumerator(s, s_Separator,
                OpeningParenthesis, ClosingParenthesis, StringSplitOptions.RemoveEmptyEntries);
            parts.MoveNext();
            var first = parts.Current;
            if (parts.Count == 1 && first.SequenceEqual("none".AsSpan())) return default;
            if (parts.Count < 3 || parts.Count > 6) throw new FormatException();
            var inset = first.SequenceEqual("inset".AsSpan());
            if (inset) { parts.MoveNext(); first = parts.Current; }
            var offsetX = first.ParseDouble(CultureInfo.InvariantCulture);
            parts.MoveNext();
            var offsetY = parts.Current.ParseDouble(CultureInfo.InvariantCulture);
            if (!parts.MoveNext()) throw new ArgumentNullException("s");
            var token3 = parts.Current;
            var has4 = parts.MoveNext();
            var token4 = parts.Current;
            var has5 = parts.MoveNext();
            var token5 = parts.Current;
            var blur = has4 ? token3.ParseDouble(CultureInfo.InvariantCulture) : 0;
            var spread = has5 ? token4.ParseDouble(CultureInfo.InvariantCulture) : 0;
            var color = Color.Parse(has5 ? token5 : has4 ? token4 : token3);
            return new BoxShadow
            {
                IsInset = inset, OffsetX = offsetX, OffsetY = offsetY,
                Blur = blur, Spread = spread, Color = color
            };
        }

        /// <summary>
        /// Determines whether two <see cref="BoxShadow"/> values are equal.
        /// </summary>
        /// <param name="left">The first <see cref="BoxShadow"/> to compare.</param>
        /// <param name="right">The second <see cref="BoxShadow"/> to compare.</param>
        /// <returns>
        /// <c>true</c> if the two <see cref="BoxShadow"/> values are equal; otherwise, <c>false</c>.
        /// </returns>
        public static bool operator ==(BoxShadow left, BoxShadow right) =>
            left.Equals(right);

        /// <summary>
        /// Determines whether two <see cref="BoxShadow"/> values are not equal.
        /// </summary>
        /// <param name="left">The first <see cref="BoxShadow"/> to compare.</param>
        /// <param name="right">The second <see cref="BoxShadow"/> to compare.</param>
        /// <returns>
        /// <c>true</c> if the two <see cref="BoxShadow"/> values are not equal; otherwise, <c>false</c>.
        /// </returns>
        public static bool operator !=(BoxShadow left, BoxShadow right) => 
            !(left == right);
    }
}
