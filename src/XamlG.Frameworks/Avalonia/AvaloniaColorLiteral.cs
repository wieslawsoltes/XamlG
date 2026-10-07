using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Recognizes the pinned color grammar without loading or executing framework types.
/// Actual color arithmetic remains a call to the application's public parser.</summary>
internal static class AvaloniaColorLiteral
{
    public static bool IsValid(RoslynTypeSystem types, string text)
    {
        if (text.Length == 0) return false;
        if (text[0] == '#')
        {
            var hex = text.Substring(1);
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(character => new string(character, 2)));
            return hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        }
        if (types.Find("Avalonia.Media.Colors")?.GetMembers().OfType<IPropertySymbol>().Any(property =>
            property.IsStatic && property.Type.HasMetadataName(AvaloniaLiteralMetadata.Color) &&
            string.Equals(property.Name, text, StringComparison.OrdinalIgnoreCase)) == true) return true;
        if (text.Length < 10) return false;
        var family = text.Substring(0, 3).ToLowerInvariant();
        if (family is not ("rgb" or "hsl" or "hsv")) return false;
        var normalized = text.Trim();
        if (!normalized.EndsWith(")", StringComparison.Ordinal)) return false;
        var start = normalized.StartsWith(family + "a(", StringComparison.OrdinalIgnoreCase) ? 5 :
            normalized.StartsWith(family + "(", StringComparison.OrdinalIgnoreCase) ? 4 : 0;
        if (start == 0 || normalized.Length < start + 6) return false;
        var parts = normalized.Substring(start, normalized.Length - start - 1).Split(',');
        if (parts.Length is not (3 or 4)) return false;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var percent = part.IndexOf('%');
            // Framework percentage parsing consumes the prefix before the first %.
            if (percent >= 0 && (family == "rgb" || index != 0)) part = part.Substring(0, percent);
            if (family == "rgb" && index < 3 && percent < 0)
            {
                if (!byte.TryParse(part, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return false;
            }
            else if (!double.TryParse(part, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return false;
        }
        return true;
    }
}
