using System.Globalization;
using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Allocation-bounded invariant drawing grammars shared by schema validation and
/// native adapters. No resource URI, markup extension or executable geometry is accepted.</summary>
public static class UiDrawingValues
{
    public const int MaximumPoints = 512;
    public const int MaximumPathSegments = 512;
    public const int MaximumDrawingText = 16384;

    public static void Validate(UiElement node)
    {
        foreach (var property in node.Properties)
        {
            switch (property.Key)
            {
                case "RenderTransform": case "LayoutTransform": ReadMatrix(property.Value.GetString()!); break;
                case "RenderTransformOrigin": ReadOrigin(property.Value.GetString()!); break;
                case "Clip": ValidatePath(property.Value.GetString()!); break;
                case "Data" when node.Type == "Path": ValidatePath(property.Value.GetString()!); break;
                case "Points" when node.Type is "Polyline" or "Polygon": ReadPoints(property.Value.GetString()!); break;
                case "StrokeDashArray": ReadDashes(property.Value.GetString()!); break;
                case "FontFamily":
                    var family = property.Value.GetString()!;
                    if (string.IsNullOrWhiteSpace(family) || family.Length > 128 || family.Any(c => char.IsControl(c) || c is ':' or '/' or '\\' or '#' or '{' or '}'))
                        throw Invalid("Use a font family name, not an asset URI or markup expression.");
                    break;
            }
        }
    }

    public static double[] ReadMatrix(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var value = source.AsSpan().Trim();
        if (value.SequenceEqual("none")) return [1, 0, 0, 1, 0, 0];
        if (!value.StartsWith("matrix(", StringComparison.Ordinal) || !value.EndsWith(")", StringComparison.Ordinal))
            throw Invalid("Use matrix(m11,m12,m21,m22,offsetX,offsetY) or none.");
        var values = ReadNumbers(value[7..^1], 6);
        if (values.Length != 6) throw Invalid("A two-dimensional matrix has six coefficients.");
        return values;
    }

    public static (double X, double Y, bool Relative) ReadOrigin(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 256) throw Invalid("Transform origin is too long.");
        var parts = source.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw Invalid("A transform origin has two coordinates.");
        var relative = parts[0].EndsWith('%');
        if (parts[1].EndsWith('%') != relative) throw Invalid("Transform origin units must agree.");
        var x = ReadNumbers(relative ? parts[0].AsSpan(0, parts[0].Length - 1) : parts[0].AsSpan(), 1);
        var y = ReadNumbers(relative ? parts[1].AsSpan(0, parts[1].Length - 1) : parts[1].AsSpan(), 1);
        if (x.Length != 1 || y.Length != 1) throw Invalid("A transform origin has two coordinates.");
        return (relative ? x[0] / 100 : x[0], relative ? y[0] / 100 : y[0], relative);
    }

    public static double[] ReadPoints(string source)
    {
        var values = ReadNumbers(source.AsSpan(), MaximumPoints * 2);
        if (values.Length % 2 != 0) throw Invalid("Points require complete x,y pairs.");
        return values;
    }

    public static double[] ReadDashes(string source)
    {
        var values = ReadNumbers(source.AsSpan(), 64, 0, 10000);
        if (values.Length != 0 && !values.Any(value => value > 0)) throw Invalid("A nonempty dash pattern must contain a positive length.");
        return values;
    }

    public static double[] ReadNumbers(ReadOnlySpan<char> source, int maximumCount, decimal minimum = -1000000, decimal maximum = 1000000)
    {
        if (maximumCount < 1 || maximumCount > 4096) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if (source.Length > MaximumDrawingText) throw Invalid("Drawing text exceeds its budget.");
        var reader = new NumberReader(source);
        var values = new List<double>(Math.Min(maximumCount, 16));
        while (!reader.End)
        {
            if (values.Count == maximumCount) throw Invalid("Drawing value count exceeds its budget.");
            var number = reader.Number(values.Count != 0, requireSeparator: values.Count != 0);
            if (number < minimum || number > maximum) throw Invalid("Drawing coordinate is outside its bounds.");
            values.Add((double)number);
        }
        return values.ToArray();
    }

    /// <summary>Validates SVG-compatible M/L/H/V/C/S/Q/T/A/Z path data, including implicit
    /// repetitions, relative commands, exponent notation, and Avalonia's optional F0/F1 prefix.</summary>
    public static void ValidatePath(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumDrawingText) throw Invalid("Path text exceeds its budget.");
        var reader = new NumberReader(source.AsSpan());
        if (reader.Peek is 'F' or 'f')
        {
            reader.Take();
            if (reader.Take() is not ('0' or '1')) throw Invalid("Path fill rule must be F0 or F1.");
        }
        var command = '\0';
        var started = false;
        var segments = 0;
        Span<decimal> arguments = stackalloc decimal[7];
        while (!reader.End)
        {
            if (++segments > MaximumPathSegments) throw Invalid("Path segment count exceeds its budget.");
            var explicitCommand = char.IsAsciiLetter(reader.Peek);
            if (explicitCommand) command = char.ToUpperInvariant(reader.Take());
            if (!started && command != 'M') throw Invalid("A path must start with a move command.");
            var count = command switch
            {
                'M' or 'L' or 'T' => 2, 'H' or 'V' => 1, 'C' => 6,
                'S' or 'Q' => 4, 'A' => 7, 'Z' => 0,
                _ => throw Invalid("Unknown or missing path command.")
            };
            if (count == 0) { command = '\0'; continue; }
            for (var i = 0; i < count; i++)
            {
                arguments[i] = reader.Number(i != 0 || !explicitCommand);
                if (arguments[i] is < -1000000 or > 1000000) throw Invalid("Path coordinate is outside its bounds.");
            }
            if (command == 'A' && (arguments[0] < 0 || arguments[1] < 0 || arguments[3] is not (0 or 1) || arguments[4] is not (0 or 1)))
                throw Invalid("Arc radii must be nonnegative and arc flags must be zero or one.");
            started = true;
            if (command == 'M') command = 'L';
        }
    }

    private static UiException Invalid(string message) => new("invalid_property", message);

    private ref struct NumberReader(ReadOnlySpan<char> source)
    {
        private readonly ReadOnlySpan<char> _source = source;
        private int _position;
        internal bool End { get { var position = _position; while (position < _source.Length && char.IsWhiteSpace(_source[position])) position++; return position == _source.Length; } }
        internal char Peek { get { White(); return _position == _source.Length ? '\0' : _source[_position]; } }
        internal char Take() { White(); return _position == _source.Length ? throw Invalid("Incomplete drawing literal.") : _source[_position++]; }
        private void White() { while (_position < _source.Length && char.IsWhiteSpace(_source[_position])) _position++; }
        internal decimal Number(bool allowComma, bool requireSeparator = false)
        {
            var before = _position;
            White();
            if (_position < _source.Length && _source[_position] == ',')
            {
                if (!allowComma) throw Invalid("Unexpected comma in drawing literal.");
                _position++; White();
            }
            if (requireSeparator && before == _position) throw Invalid("Separate drawing values with commas or whitespace.");
            var start = _position;
            if (_position < _source.Length && _source[_position] is '+' or '-') _position++;
            var digits = 0;
            while (_position < _source.Length && char.IsAsciiDigit(_source[_position])) { digits++; _position++; }
            if (_position < _source.Length && _source[_position] == '.')
            {
                _position++;
                while (_position < _source.Length && char.IsAsciiDigit(_source[_position])) { digits++; _position++; }
            }
            if (digits == 0) throw Invalid("Expected a finite drawing number.");
            if (_position < _source.Length && _source[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < _source.Length && _source[_position] is '+' or '-') _position++;
                var exponent = _position;
                while (_position < _source.Length && char.IsAsciiDigit(_source[_position])) _position++;
                if (_position == exponent) throw Invalid("Incomplete numeric exponent.");
            }
            if (!decimal.TryParse(_source[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw Invalid("Expected a bounded finite drawing number.");
            return value;
        }
    }
}
