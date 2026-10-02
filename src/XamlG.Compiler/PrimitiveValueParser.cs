using System.Globalization;
using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;

internal static class PrimitiveValueParser
{
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
            SpecialType.System_Single => typeof(float), SpecialType.System_Double => typeof(double), SpecialType.System_Decimal => typeof(decimal), _ => null
        };
        if (clrType == null) return false;
        if (type == SpecialType.System_Boolean && (text == "0" || text == "1")) { value = text == "1"; return true; }
        if (type is SpecialType.System_Single or SpecialType.System_Double)
        {
            if (text == "INF") { value = type == SpecialType.System_Single ? (object)float.PositiveInfinity : double.PositiveInfinity; return true; }
            if (text == "-INF") { value = type == SpecialType.System_Single ? (object)float.NegativeInfinity : double.NegativeInfinity; return true; }
        }
        try { value = Convert.ChangeType(text, clrType, CultureInfo.InvariantCulture); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
