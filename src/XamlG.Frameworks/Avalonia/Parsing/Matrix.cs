using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct Matrix(double M11, double M12, double M13, double M21, double M22, double M23, double M31, double M32, double M33)
{
    public Matrix(double m11, double m12, double m21, double m22, double m31, double m32) : this(m11, m12, 0, m21, m22, 0, m31, m32, 1) { }

    public static Matrix Parse(string s)
    {
        // initialize to satisfy compiler - only used when retrieved from string.
        double v8 = 0;
        double v9 = 0;

        using (var tokenizer = new SpanStringTokenizer(s, CultureInfo.InvariantCulture, exceptionMessage: "Invalid Matrix."))
        {
            var v1 = tokenizer.ReadDouble();
            var v2 = tokenizer.ReadDouble();
            var v3 = tokenizer.ReadDouble();
            var v4 = tokenizer.ReadDouble();
            var v5 = tokenizer.ReadDouble();
            var v6 = tokenizer.ReadDouble();
            var persp = tokenizer.TryReadDouble(out var v7);
            persp = persp && tokenizer.TryReadDouble(out v8);
            persp = persp && tokenizer.TryReadDouble(out v9);

            if (persp)
                return new Matrix(v1, v2, v7, v3, v4, v8, v5, v6, v9);
            else
                return new Matrix(v1, v2, v3, v4, v5, v6);
        }
    }
}
