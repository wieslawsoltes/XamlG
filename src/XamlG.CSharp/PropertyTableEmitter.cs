using Microsoft.CodeAnalysis;
using System.Runtime.CompilerServices;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class PropertyAccessor(string type, string name, string get, string set)
{
    private static readonly ConditionalWeakTable<IPropertySymbol, PropertyAccessor> Properties = new();
    public string Type { get; } = type;
    public string Name { get; } = name;
    public string Get { get; } = get;
    public string Set { get; } = set;
    public string Key { get; } = type + "\0" + name + "\0" + get + "\0" + set;

    // An inherited CLR property has the same editing accessor on every derived
    // owner. Use its resolved declaring type, preserving virtual dispatch and
    // keeping hidden/overridden and closed generic members distinct. Custom
    // setters retain their original receiver and conversion contracts.
    public static ITypeSymbol ReceiverType(ITypeSymbol owner, BoundMember member) =>
        owner.IsReferenceType && member is { Kind: BoundMemberKind.Property, StaticSetter: null,
            Symbol: IPropertySymbol { IsStatic: false } property }
            ? property.ContainingType : owner;

    public static PropertyAccessor Create(ITypeSymbol owner, BoundMember member)
    {
        owner = ReceiverType(owner, member);
        if (member is { Kind: BoundMemberKind.Property, StaticSetter: null, Symbol: IPropertySymbol { IsStatic: false } property } &&
            owner.IsReferenceType && SymbolEqualityComparer.Default.Equals(owner, property.ContainingType) &&
            member.Name == property.Name && SymbolEqualityComparer.Default.Equals(member.ValueType, property.Type))
        {
            // Only strings survive in the value; weak symbol keys do not retain
            // compilations across incremental generator or editor sessions.
            if (Properties.TryGetValue(property, out var cached)) return cached;
            var created = CreateCore(owner, member);
            return Properties.GetValue(property, _ => created);
        }
        return CreateCore(owner, member);
    }

    private static PropertyAccessor CreateCore(ITypeSymbol owner, BoundMember member)
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
