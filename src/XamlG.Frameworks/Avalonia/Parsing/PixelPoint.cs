using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct PixelPoint(int X, int Y)
{
    public static PixelPoint Parse(string s)
    {
        using (var tokenizer = new SpanStringTokenizer(s, CultureInfo.InvariantCulture, exceptionMessage: "Invalid PixelPoint."))
        {
            return new PixelPoint(
                tokenizer.ReadInt32(),
                tokenizer.ReadInt32());
        }
    }
}
