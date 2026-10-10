using System.Text;

namespace XamlG.CSharp;

/// <summary>Exact invariant, UTF-16-length framing for compiler-owned metadata.</summary>
internal static class MetadataRecordEncoding
{
    public static void AppendNumber(StringBuilder target, int value)
    {
        // Avoid one temporary decimal string for every span, count and text length.
        // Convert through Int64 so Int32.MinValue is valid even in checked builds.
        var magnitude = value < 0 ? (uint)-(long)value : (uint)value;
        if (value < 0) target.Append('-');
        if (magnitude < 10) target.Append((char)('0' + magnitude));
        else
        {
            Span<char> digits = stackalloc char[10];
            var start = digits.Length;
            do
            {
                digits[--start] = (char)('0' + magnitude % 10);
                magnitude /= 10;
            } while (magnitude != 0);
            for (; start < digits.Length; start++) target.Append(digits[start]);
        }
        target.Append(':');
    }

    public static void AppendText(StringBuilder target, string? value)
    {
        AppendNumber(target, value?.Length ?? -1);
        target.Append(value);
    }
}
