using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct RelativeRect(double X, double Y, double Width, double Height, RelativeUnit Unit)
{
    private const char PercentChar = '%';

    public static RelativeRect Parse(string s)
    {
        using (var tokenizer = new SpanStringTokenizer(s, exceptionMessage: "Invalid RelativeRect."))
        {
            var x = tokenizer.ReadSpan();
            var y = tokenizer.ReadSpan();
            var width = tokenizer.ReadSpan();
            var height = tokenizer.ReadSpan();

            var unit = RelativeUnit.Absolute;
            var scale = 1.0;

            var xRelative = x.EndsWith("%".AsSpan(), StringComparison.Ordinal);
            var yRelative = y.EndsWith("%".AsSpan(), StringComparison.Ordinal);
            var widthRelative = width.EndsWith("%".AsSpan(), StringComparison.Ordinal);
            var heightRelative = height.EndsWith("%".AsSpan(), StringComparison.Ordinal);

            if (xRelative && yRelative && widthRelative && heightRelative)
            {
                x = x.TrimEnd(PercentChar);
                y = y.TrimEnd(PercentChar);
                width = width.TrimEnd(PercentChar);
                height = height.TrimEnd(PercentChar);

                unit = RelativeUnit.Relative;
                scale = 0.01;
            }
            else if (xRelative || yRelative || widthRelative || heightRelative)
            {
                throw new FormatException("If one coordinate is relative, all must be.");
            }

            return new RelativeRect(
                SpanHelpers.ParseDouble(x, CultureInfo.InvariantCulture) * scale,
                SpanHelpers.ParseDouble(y, CultureInfo.InvariantCulture) * scale,
                SpanHelpers.ParseDouble(width, CultureInfo.InvariantCulture) * scale,
                SpanHelpers.ParseDouble(height, CultureInfo.InvariantCulture) * scale,
                unit);
        }
    }
}
