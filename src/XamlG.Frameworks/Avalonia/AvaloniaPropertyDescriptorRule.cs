using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

public sealed class AvaloniaPropertyDescriptorRule : IXamlMemberBindingRule
{
    public BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope)
    {
        // The upstream content-property pass runs after registration resolution.
        if (member.IsImplicitContent) return member;
        var propertyType = context.Types.Find(AvaloniaMetadata.Property);
        if (propertyType == null) return member;
        var owner = member.Symbol.ContainingType;
        var field = owner.Members(member.Name + AvaloniaMetadata.PropertySuffix).OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.IsStatic && context.Types.IsAccessible(f) && context.Types.Compilation.ClassifyCommonConversion(f.Type, propertyType).IsImplicit);
        return field == null ? member : AvaloniaTemplatePriority.Apply(context,
            member with { TargetDescriptor = new BoundStaticExpression(field, field.Type, member.Span) }, field);
    }
}
