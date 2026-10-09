using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D Parse(string s)
    {
        using (var tokenizer = new SpanStringTokenizer(s, CultureInfo.InvariantCulture, exceptionMessage: "Invalid Vector."))
        {
            return new Vector3D(
                tokenizer.ReadDouble(),
                tokenizer.ReadDouble(),
                tokenizer.ReadDouble()
            );
        }
    }
}
