using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct PixelSize(int Width, int Height)
{
    public static PixelSize Empty => default;

    public static PixelSize Parse(string s)
    {
        if (TryParse(s, out var result))
        {
            return result;
        }
        throw new FormatException("Invalid PixelSize.");
    }

    public static bool TryParse(string? source,
        out PixelSize result)
    {
        result = Empty;
        if(source == null || source.Length == 0)
        {
            return false;
        }
        using (var tokenizer = new SpanStringTokenizer(source, exceptionMessage: "Invalid PixelSize."))
        {
            if (tokenizer.TryReadInt32(out var w) && tokenizer.TryReadInt32(out var h))
            {
                result = new(w, h);
                return true;
            }
            return false;
        }
    }
}
