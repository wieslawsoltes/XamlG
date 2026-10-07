using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal static class AvaloniaDataContextInference
{
    public static bool TryRead(BindingContext context, ObjectBindingBuilder target, BindingPropertyValue property, out ITypeSymbol? dataType)
    {
        dataType = null;
        var nodes = property.Values.Where(node => node is XamlElementSyntax || node is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value)).ToArray();
        if (nodes.Length != 1) return false;
        var readInput = CompiledBindingValueReader.Read(context, target, nodes[0], property.Scope, out var type);
        if (type == null) return false;
        if (AvaloniaCompiledBindingRule.ShouldCompile(target, type))
        {
            if (readInput != null) dataType = AvaloniaCompiledBindingRule.Bind(context, target,
                context.Types.Special(SpecialType.System_Object), nodes[0].Span, readInput, inferDataContext: true)?.ValueType;
            return true;
        }
        if (nodes[0] is XamlElementSyntax && context.Types.MarkupExtensionMethod(type) == null && AvaloniaStyleScope.Is(type, AvaloniaMetadata.BindingBase))
            return false;
        if (AvaloniaStyleScope.Is(type, AvaloniaMetadata.BindingBase) ||
            type.HasMetadataName(AvaloniaBindingMetadata.ReflectionExtension) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension))
            return true;
        dataType = type;
        return true;
    }

}
