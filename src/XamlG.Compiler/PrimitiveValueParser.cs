using System.Globalization;
using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;

internal static class PrimitiveValueParser
{
    private static readonly string[] CompleteDateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"
    ];
    public static bool IsScalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return IsScalar(nullable.TypeArguments[0]);
        // Decimal and DateTime have Parse-based XAML contracts. Leave malformed
        // or runtime-dependent values on that path instead of changing when their
        // FormatException/OverflowException is observed.
        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;
    }

    public static bool TryParse(string text, SpecialType type, out object? value)
    {
        value = null;
        if (type is SpecialType.System_String or SpecialType.System_Object) { value = text; return true; }
        if (type == SpecialType.System_Char) { if (text.Length != 1) return false; value = text[0]; return true; }
        if (type == SpecialType.System_DateTime)
        {
            // Only complete, zone-free invariant dates are stable build-time values.
            // Missing calendar fields and offsets can depend on the runtime's current
            // date or local time zone, so keep their existing Parse call intact.
            if (!DateTime.TryParseExact(text.Trim(), CompleteDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                date != parsed || parsed.Kind != DateTimeKind.Unspecified) return false;
            value = date;
            return true;
        }
        var clrType = type switch
        {
            SpecialType.System_Boolean => typeof(bool), SpecialType.System_Byte => typeof(byte), SpecialType.System_SByte => typeof(sbyte),
            SpecialType.System_Int16 => typeof(short), SpecialType.System_UInt16 => typeof(ushort), SpecialType.System_Int32 => typeof(int),
            SpecialType.System_UInt32 => typeof(uint), SpecialType.System_Int64 => typeof(long), SpecialType.System_UInt64 => typeof(ulong),
            SpecialType.System_Single => typeof(float), SpecialType.System_Double => typeof(double),
            SpecialType.System_Decimal => typeof(decimal), _ => null
        };
        if (clrType == null) return false;
        try { value = Convert.ChangeType(text, clrType, CultureInfo.InvariantCulture); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
