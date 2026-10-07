using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Styling;

internal static class AvaloniaTemplatePriority
{
    public static BoundMember Apply(BindingContext context, BoundMember member, IFieldSymbol field)
    {
        // Upstream resolves implicit content after the registered-property transform;
        // only explicit registered assignments acquire the priority-aware setter.
        if (!member.CanWrite || member.IsImplicitContent || field.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } property ||
            !(property.HasMetadataName(AvaloniaRegisteredSetterMetadata.StyledProperty) || property.HasMetadataName(AvaloniaRegisteredSetterMetadata.AttachedProperty)) ||
            !SymbolEqualityComparer.Default.Equals(property.TypeArguments[0], member.ValueType) ||
            !context.Ancestors.Any(ancestor => AvaloniaStyleScope.IsTemplate(ancestor.Type) ||
                ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.Selector, out var selector) && selector.HasTemplateScope)) return member;
        var method = context.Types.Find(AvaloniaRegisteredSetterMetadata.Adapter)?.GetMembers(AvaloniaRegisteredSetterMetadata.AssignTemplate)
            .OfType<IMethodSymbol>().SingleOrDefault(candidate => candidate.IsStatic && candidate.Arity == 1 && candidate.Parameters.Length == 3 &&
                candidate.Parameters[0].Type.HasMetadataName(AvaloniaMetadata.Object) &&
                candidate.Parameters[1].Type.HasMetadataName(AvaloniaRegisteredSetterMetadata.StyledProperty) && context.Types.IsAccessible(candidate));
        if (method == null)
        {
            context.Report("XG3002", "Template-priority assignment requires the matching XamlG.AvaloniaRuntime contract.", member.Span);
            return member;
        }
        return member with { StaticSetter = new(method.Construct(member.ValueType), ImmutableArray.Create(field)) };
    }
}
