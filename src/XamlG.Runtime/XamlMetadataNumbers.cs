using System.Globalization;

namespace XamlG.Runtime;

/// <summary>Bounded integer scanning shared by table framing and source-record decoding.
/// Generated decimal fields allocate no temporary strings and require no System.Memory dependency.</summary>
internal static class XamlMetadataNumbers
{
    public static bool TryRead(string text, int start, int end, bool allowSign, out int value)
    {
        value = 0;
        var position = start;
        if (position == end) return false;
        var negative = false;
        if (allowSign && text[position] is '+' or '-')
        {
            negative = text[position++] == '-';
            if (position == end) return false;
        }
        uint magnitude = 0;
        var lastDigit = negative ? 8U : 7U;
        for (; position < end; position++)
        {
            var digit = (uint)(text[position] - '0');
            if (digit > 9)
            {
                // Keep the exact historical BCL grammar for unusual externally
                // supplied spellings (including trailing NULs). Generated records
                // never enter this compatibility path.
                return int.TryParse(text.Substring(start, end - start),
                    allowSign ? NumberStyles.AllowLeadingSign : NumberStyles.None,
                    CultureInfo.InvariantCulture, out value);
            }
            if (magnitude > 214748364U || magnitude == 214748364U && digit > lastDigit) return false;
            magnitude = magnitude * 10 + digit;
        }
        value = negative ? unchecked(-(int)magnitude) : (int)magnitude;
        return true;
    }
}
