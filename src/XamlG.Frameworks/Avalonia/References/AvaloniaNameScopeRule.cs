using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Declares literal names for binding and registers string-valued INamed.Name
/// assignments after their setters, including values supplied at runtime.</summary>
public sealed class AvaloniaNameScopeRule : IXamlObjectBindingRule, IXamlMemberBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        foreach (var name in AvaloniaLiteralName.Read(context, target))
        {
            if (target.Name == null)
            {
                target.Name = name.Value;
                if (context.FindName(target.NameScopeId, name.Value) == null) target.Key = "s" + target.NameScopeId + ":" + name.Value;
            }
            context.RegisterName(target.NameScopeId, name.Value, target.Type, name.Span, allowDuplicate: true);
        }
    }

    public BoundMember Bind(BindingContext context, ITypeSymbol target, BoundMember member, NamespaceScope scope) =>
        member.Name == "Name" && member.Symbol is IPropertySymbol ? member with { AllowRepeatedAssignments = true } : member;

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        for (var index = 0; index < target.Assignments.Count; index++)
        {
            if (target.Assignments[index] is not BoundSetAssignment assignment ||
                assignment.Member.Name != "Name" || assignment.Member.Symbol is not IPropertySymbol property ||
                !property.ContainingType.AllInterfaces.Any(type => type.HasMetadataName(AvaloniaMetadata.Named))) continue;
            var value = assignment.Value;
            // Setter casts do not change the provider's static type for name registration.
            while (value is BoundCastExpression cast) value = cast.Value;
            if (value is not BoundConstantExpression { Value: null } && value.Type?.SpecialType == SpecialType.System_String)
                target.Assignments[index] = assignment with { RegisterName = true };
        }
    }
}
