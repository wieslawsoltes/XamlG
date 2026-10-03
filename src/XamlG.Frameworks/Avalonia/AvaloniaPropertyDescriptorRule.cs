using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

public sealed class AvaloniaPropertyDescriptorRule : IXamlMemberBindingRule
{
    public BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope)
    {
        var propertyType = context.Types.Find(AvaloniaMetadata.Property);
        if (propertyType == null) return member;
        var owner = member.Kind == BoundMemberKind.AttachedProperty ? member.Symbol.ContainingType : targetType;
        var field = owner.Members(member.Name + AvaloniaMetadata.PropertySuffix).OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.IsStatic && context.Types.IsAccessible(f) && context.Types.Compilation.ClassifyCommonConversion(f.Type, propertyType).IsImplicit);
        return field == null ? member : member with { TargetDescriptor = new BoundStaticExpression(field, field.Type, member.Span) };
    }
}
