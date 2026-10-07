using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal static class CompiledBindingValueReader
{
    public static Func<CompiledBindingInput?>? Read(BindingContext context, ObjectBindingBuilder target,
        XamlSyntaxNode value, NamespaceScope scope, out INamedTypeSymbol? type)
    {
        type = null;
        if (value is XamlTextSyntax text)
        {
            if (!text.Value.StartsWith("{", StringComparison.Ordinal) || text.Value.StartsWith("{}", StringComparison.Ordinal)) return null;
            var markup = MarkupExtensionParser.Parse(text.Value, text.Span, _ => { });
            if (markup == null || IsIntrinsic(scope, markup.Name)) return null;
            type = context.ResolveType(markup.Name, scope, markup.Span, report: false, extension: true);
            if (type != null && AvaloniaCompiledBindingRule.ShouldCompile(target, type))
                return () => CompiledBindingInputReader.FromMarkup(context, markup, scope);
        }
        else if (value is XamlElementSyntax element)
        {
            if (IsIntrinsic(scope.Push(element), element.Name)) return null;
            type = context.Values.PeekNodeType(element, scope) as INamedTypeSymbol;
            if (type != null && AvaloniaCompiledBindingRule.ShouldCompile(target, type) && context.Types.Find(AvaloniaBindingMetadata.CompiledExtension) is { } compiled)
                return () => CompiledBindingInputReader.FromElement(context, element, scope, compiled);
        }
        return null;
    }

    private static bool IsIntrinsic(NamespaceScope scope, string name) =>
        scope.Expand(name).Namespace is { } ns && XamlNames.IsLanguage(ns);
}
