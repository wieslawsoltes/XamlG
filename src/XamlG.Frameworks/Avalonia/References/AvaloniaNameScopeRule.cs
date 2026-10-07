using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Declares literal names for binding and registers string-valued INamed.Name
/// assignments after their setters, including values supplied at runtime.</summary>
public sealed class AvaloniaNameScopeRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        if (AvaloniaLiteralName.Read(context, target) is not { } name) return;
        if (!SyntaxFacts.IsValidIdentifier(name.Value))
        {
            context.Report("XG1012", $"Invalid XAML name '{name.Value}'.", name.Span);
            return;
        }
        if (target.Name != null)
        {
            if (!string.Equals(target.Name, name.Value, StringComparison.Ordinal))
                context.Report("XG1012", "Name and x:Name must identify the same object.", name.Span);
            return;
        }
        target.Name = name.Value;
        context.RegisterName(target.NameScopeId, name.Value, target.Type, name.Span);
    }

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
