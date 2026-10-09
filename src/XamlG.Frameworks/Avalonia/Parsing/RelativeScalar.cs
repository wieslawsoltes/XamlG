using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct RelativeScalar(double Scalar, RelativeUnit Unit)
{
    public static RelativeScalar Parse(string s)
    {
        var trimmed = s.AsSpan().Trim();
        if (trimmed.EndsWith("%".AsSpan(), StringComparison.Ordinal))
            return new RelativeScalar(trimmed.TrimEnd('%').ParseDouble(CultureInfo.InvariantCulture) * 0.01,
                RelativeUnit.Relative);

        return new RelativeScalar(trimmed.ParseDouble(CultureInfo.InvariantCulture), RelativeUnit.Absolute);
    }
}
