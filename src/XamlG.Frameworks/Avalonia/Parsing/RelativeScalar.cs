using System;
using System.Globalization;

namespace XamlG.Frameworks.Avalonia.Parsing;

internal readonly record struct RelativeScalar(double Scalar, RelativeUnit Unit)
{
    public static RelativeScalar Parse(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.EndsWith("%", StringComparison.Ordinal))
            return new RelativeScalar(double.Parse(trimmed.TrimEnd('%'), CultureInfo.InvariantCulture) * 0.01,
                RelativeUnit.Relative);

        return new RelativeScalar(double.Parse(trimmed, CultureInfo.InvariantCulture), RelativeUnit.Absolute);
    }
}
