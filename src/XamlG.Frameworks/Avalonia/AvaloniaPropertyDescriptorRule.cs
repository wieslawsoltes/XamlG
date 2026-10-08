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
        var registration = AvaloniaRegisteredPropertyResolver.Find(context, owner, member.Name);
        return registration == null ? member : AvaloniaTemplatePriority.Apply(context,
            member with { TargetDescriptor = registration.Reference(member.Span) }, registration.FieldType);
    }
}
