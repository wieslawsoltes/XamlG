using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
namespace XamlG.CSharp;
internal static class CSharpNames
{
    public const string Context = "global::XamlG.Runtime.XamlRuntimeContext";
    public const string Session = "global::XamlG.Runtime.XamlRuntimeSession";
    public const string Provider = "global::System.IServiceProvider";
    public const string InvariantCulture = "global::System.Globalization.CultureInfo.InvariantCulture";
    public static string Identifier(string value) => "@" + value;
    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
    public static string StableId(string value)
    {
        using var hash = SHA256.Create(); return StableId(hash, value);
    }
    internal static string StableId(SHA256 hash, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        return StableId(hash, encoded, encoded.Length);
    }
    internal static string StableId(SHA256 hash, byte[] encoded, int count)
    {
        var bytes = hash.ComputeHash(encoded, 0, count);
        const string digits = "0123456789abcdef";
        var characters = new char[24];
        for (var index = 0; index < 12; index++)
        {
            characters[index * 2] = digits[bytes[index] >> 4];
            characters[index * 2 + 1] = digits[bytes[index] & 15];
        }
        return new string(characters);
    }
    public static string Method(IMethodSymbol method) => Identifier(method.Name) + (method.IsGenericMethod ? "<" + string.Join(", ", method.TypeArguments.Select(t => t.CSharpName())) + ">" : string.Empty);
    public static string MemberTarget(ISymbol member, ITypeSymbol targetType, string target) =>
        SymbolEqualityComparer.Default.Equals(targetType.Members(member.Name).FirstOrDefault(candidate => candidate.Kind == member.Kind), member)
            ? target : "((" + member.ContainingType.CSharpName() + ")" + target + ")";
    public static string Constant(object? value) => value switch
    {
        null => "null", string text => Literal(text), char character => SymbolDisplay.FormatLiteral(character, true), bool boolean => boolean ? "true" : "false",
        byte number => "(byte)" + number.ToString(CultureInfo.InvariantCulture), sbyte number => "(sbyte)" + number.ToString(CultureInfo.InvariantCulture),
        short number => "(short)" + number.ToString(CultureInfo.InvariantCulture), ushort number => "(ushort)" + number.ToString(CultureInfo.InvariantCulture),
        int number => number == int.MinValue ? "global::System.Int32.MinValue" : number.ToString(CultureInfo.InvariantCulture),
        uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
        long number => number == long.MinValue ? "global::System.Int64.MinValue" : number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        float number => float.IsNaN(number) ? "global::System.Single.NaN" : float.IsPositiveInfinity(number) ? "global::System.Single.PositiveInfinity" : float.IsNegativeInfinity(number) ? "global::System.Single.NegativeInfinity" : number.ToString("R", CultureInfo.InvariantCulture) + "F",
        double number => double.IsNaN(number) ? "global::System.Double.NaN" : double.IsPositiveInfinity(number) ? "global::System.Double.PositiveInfinity" : double.IsNegativeInfinity(number) ? "global::System.Double.NegativeInfinity" : number.ToString("R", CultureInfo.InvariantCulture) + "D",
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "M",
        _ => throw new ArgumentException("Only CLR scalar constants may reach the source emitter.", nameof(value))
    };
}
