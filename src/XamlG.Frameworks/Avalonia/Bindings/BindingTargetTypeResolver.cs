using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Bindings in a setter execute on the styled control, not on the Setter object.</summary>
internal static class BindingTargetTypeResolver
{
    public static INamedTypeSymbol Resolve(BindingContext context, ObjectBindingBuilder target) =>
        AvaloniaStyleScope.Is(target.Type, AvaloniaStyleMetadata.Setter)
            ? AvaloniaStyleObjectRule.FindTarget(context, target) ?? target.Type
            : target.Type;

    public static INamedTypeSymbol? FindTemplateOwner(BindingContext context, ObjectBindingBuilder template)
    {
        var contract = context.Types.Find(AvaloniaBindingMetadata.TemplatedControl);
        foreach (var ancestor in context.Ancestors)
        {
            if (ReferenceEquals(ancestor, template)) continue;
            // A nearer setter/style declaration is authoritative. Do not search through
            // its scope to an unrelated control containing the style's resources.
            if (ancestor.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var declared))
                return declared;
            if (contract != null && context.Types.Compilation.ClassifyCommonConversion(ancestor.Type, contract).IsImplicit)
                return ancestor.Type;
            if (AvaloniaStyleScope.IsTemplate(ancestor.Type) ||
                AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.ControlTheme) ||
                AvaloniaStyleScope.Is(ancestor.Type, AvaloniaStyleMetadata.Style))
                return null;
        }
        return null;
    }
}
