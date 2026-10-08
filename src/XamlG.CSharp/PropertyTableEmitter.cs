using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed record PropertyAccessor(string Type, string Name, string Get, string Set)
{
    public string Key => Type + "\0" + Name + "\0" + Get + "\0" + Set;

    public static PropertyAccessor Create(ITypeSymbol owner, BoundMember member)
    {
        var receiver = "((" + owner.CSharpName() + ")__target)";
        return new(member.ValueType.TypeKind == TypeKind.Dynamic ? "object" : member.ValueType.CSharpName(), member.Name,
            AssignmentEmitter.Get(member, owner, receiver),
            AssignmentEmitter.SetNonInit(member, owner, receiver, "(" + member.ValueType.CSharpName() + ")__value!"));
    }
}

internal static class PropertyTableEmitter
{
    public static void Emit(CSharpWriter writer, IReadOnlyList<PropertyAccessor> accessors,
        string field, string get, string set, string accessibility)
    {
        writer.Line(accessibility + " static readonly global::XamlG.Runtime.XamlPropertyTable " + field + " = new(new global::System.Type[] { " +
            string.Join(", ", accessors.Select(accessor => "typeof(" + accessor.Type + ")")) + " }, new string[] { " +
            string.Join(", ", accessors.Select(accessor => CSharpNames.Literal(accessor.Name))) + " }, " + get + ", " + set + ");");
        writer.Open("private static object? " + get + "(object __target, int __index)");
        writer.Open("switch (__index)");
        for (var i = 0; i < accessors.Count; i++) writer.Line("case " + i + ": return " + accessors[i].Get + ";");
        writer.Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(__index));");
        writer.Close(); writer.Close();
        writer.Open("private static void " + set + "(object __target, int __index, object? __value)");
        writer.Open("switch (__index)");
        for (var i = 0; i < accessors.Count; i++) writer.Line("case " + i + ": " + accessors[i].Set + "; return;");
        writer.Line("default: throw new global::System.ArgumentOutOfRangeException(nameof(__index));");
        writer.Close(); writer.Close();
    }
}
