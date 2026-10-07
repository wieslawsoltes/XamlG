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
        INamedTypeSymbol? type;
        Func<CompiledBindingInput?>? readInput = null;
        if (nodes[0] is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return false;
            var markup = MarkupExtensionParser.Parse(text.Value, text.Span, _ => { });
            if (markup == null || IsIntrinsic(property.Scope, markup.Name)) return false;
            type = context.ResolveType(markup.Name, property.Scope, markup.Span, report: false, extension: true);
            if (type != null && AvaloniaCompiledBindingRule.ShouldCompile(target, type))
                readInput = () => CompiledBindingInputReader.FromMarkup(context, markup, property.Scope);
        }
        else if (nodes[0] is XamlElementSyntax element)
        {
            var scope = property.Scope.Push(element);
            if (IsIntrinsic(scope, element.Name)) return false;
            type = context.Values.PeekNodeType(element, property.Scope) as INamedTypeSymbol;
            if (type != null && AvaloniaCompiledBindingRule.ShouldCompile(target, type) && context.Types.Find(AvaloniaBindingMetadata.CompiledExtension) is { } compiled)
                readInput = () => CompiledBindingInputReader.FromElement(context, element, property.Scope, compiled);
            else if (type != null && context.Types.MarkupExtensionMethod(type) == null && AvaloniaStyleScope.Is(type, AvaloniaMetadata.BindingBase))
                return false;
        }
        else return false;
        if (type == null) return false;
        if (AvaloniaCompiledBindingRule.ShouldCompile(target, type))
        {
            if (readInput != null) dataType = AvaloniaCompiledBindingRule.Bind(context, target,
                context.Types.Special(SpecialType.System_Object), nodes[0].Span, readInput, inferDataContext: true)?.ValueType;
            return true;
        }
        if (AvaloniaStyleScope.Is(type, AvaloniaMetadata.BindingBase) ||
            type.HasMetadataName(AvaloniaBindingMetadata.ReflectionExtension) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension))
            return true;
        dataType = type;
        return true;
    }

    private static bool IsIntrinsic(NamespaceScope scope, string name) =>
        scope.Expand(name).Namespace is { } ns && XamlNames.IsLanguage(ns);
}
