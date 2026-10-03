using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Bindings in a setter execute on the styled control, not on the Setter object.</summary>
internal static class BindingTargetTypeResolver
{
    public static INamedTypeSymbol Resolve(BindingContext context, ObjectBindingBuilder target) =>
        target.Type.HasMetadataName(AvaloniaStyleMetadata.Setter)
            ? AvaloniaStyleObjectRule.FindTarget(context, target) ?? target.Type
            : target.Type;

    public static INamedTypeSymbol? FindTemplateOwner(BindingContext context, ObjectBindingBuilder template)
    {
        var contract = context.Types.Find(AvaloniaBindingMetadata.TemplatedControl);
        if (contract == null) return null;
        return context.Ancestors.FirstOrDefault(ancestor => !ReferenceEquals(ancestor, template) &&
            context.Types.Compilation.ClassifyCommonConversion(ancestor.Type, contract).IsImplicit)?.Type;
    }
}
