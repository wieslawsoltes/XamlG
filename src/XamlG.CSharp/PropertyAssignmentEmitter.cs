using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares a typed setter and editing registration after the caller has evaluated its value.</summary>
internal static class PropertyAssignmentEmitter
{
    internal static bool TryKey(BoundSetAssignment assignment, BoundObject owner, out string key)
    {
        key = string.Empty;
        if (!owner.Type.IsReferenceType || assignment.RegisterName || assignment.Value.Type?.TypeKind == TypeKind.Dynamic ||
            assignment.Member is not { Kind: BoundMemberKind.Property, StaticSetter: null,
                Symbol: IPropertySymbol { IsStatic: false, IsIndexer: false } property,
                Getter: { } getter, Setter: { IsInitOnly: false } setter } member ||
            member.Name != property.Name || !SymbolEqualityComparer.Default.Equals(member.ValueType, property.Type) ||
            !SymbolEqualityComparer.Default.Equals(getter, property.GetMethod) ||
            !SymbolEqualityComparer.Default.Equals(setter, property.SetMethod)) return false;
        key = PropertyAccessor.Create(owner.Type, member).Key;
        return true;
    }

    internal static bool TryEmit(EmissionContext context, BoundSetAssignment assignment, BoundObject owner,
        string target, string frame, string value)
    {
        if (context.SharedProperties is not { } properties || !TryKey(assignment, owner, out var key) ||
            !properties.Assignments.TryGetValue(key, out var shared)) return false;
        var receiver = context.UsePropertyAliases ? shared.Alias : shared.Source.TypeName;
        context.Writer.Line(receiver + "." + shared.Method + "(" + target + ", " + frame + ", " + value + ");");
        return true;
    }

    internal static void EmitHelper(CSharpWriter writer, BoundMember member, string name, int index)
    {
        var property = (IPropertySymbol)member.Symbol;
        writer.Open("internal static void " + name + "(" + property.ContainingType.CSharpName() + " __target, " +
            CSharpNames.Context + " __frame, " + (member.ValueType.TypeKind == TypeKind.Dynamic ? "object" : member.ValueType.CSharpName()) + " __value)");
        writer.Line(AssignmentEmitter.SetNonInit(member, property.ContainingType, "__target", "__value") + ";");
        writer.Line("Table.Register(__frame, " + index + ");");
        writer.Close();
    }
}
