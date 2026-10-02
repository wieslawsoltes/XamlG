using Microsoft.CodeAnalysis;
namespace XamlG.Roslyn;

internal static class XamlIntrinsicTypes
{
    public static SpecialType GetSpecialType(string name) => name switch
    {
        "Object" => SpecialType.System_Object, "String" => SpecialType.System_String, "Boolean" => SpecialType.System_Boolean,
        "Byte" => SpecialType.System_Byte, "SByte" => SpecialType.System_SByte, "Int16" => SpecialType.System_Int16,
        "UInt16" => SpecialType.System_UInt16, "Int32" => SpecialType.System_Int32, "UInt32" => SpecialType.System_UInt32,
        "Int64" => SpecialType.System_Int64, "UInt64" => SpecialType.System_UInt64, "Single" => SpecialType.System_Single,
        "Double" => SpecialType.System_Double, "Decimal" => SpecialType.System_Decimal, "Char" => SpecialType.System_Char,
        "Nullable" => SpecialType.System_Nullable_T, _ => SpecialType.None
    };
}
