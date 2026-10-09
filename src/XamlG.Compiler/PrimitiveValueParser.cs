using System.Globalization;
using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;

internal static class PrimitiveValueParser
{
    public static bool IsScalar(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return IsScalar(nullable.TypeArguments[0]);
        // Decimal has a Parse-based XAML contract: fold valid literals, but keep
        // malformed values on the runtime parser path instead of changing when
        // their FormatException/OverflowException is observed.
        return type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double;
    }

    public static bool TryParse(string text, SpecialType type, out object? value)
    {
        value = null;
        if (type is SpecialType.System_String or SpecialType.System_Object) { value = text; return true; }
        if (type == SpecialType.System_Char) { if (text.Length != 1) return false; value = text[0]; return true; }
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
