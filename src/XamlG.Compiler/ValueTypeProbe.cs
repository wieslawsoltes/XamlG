using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Probes provided and intrinsic value types without reporting speculative diagnostics.</summary>
internal sealed class ValueTypeProbe(BindingContext context)
{
    public ITypeSymbol? Peek(XamlSyntaxNode syntax, NamespaceScope scope, int nameScope = 0)
    {
        if (syntax is XamlTextSyntax textValue && textValue.Value.StartsWith("{", StringComparison.Ordinal) && !textValue.Value.StartsWith("{}", StringComparison.Ordinal))
        {
            var markup = MarkupExtensionParser.Parse(textValue.Value, textValue.Span, _ => { });
            if (markup == null) return context.Types.Special(SpecialType.System_Object);
            var expanded = scope.Expand(markup.Name);
            if (expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace))
            {
                var value = new IntrinsicMarkupBinder(context, report: false).Bind(markup, context.Types.Special(SpecialType.System_Object), scope);
                if (value is BoundReferenceExpression reference) return context.FindName(nameScope, reference.Name) ?? value.Type;
                return value == null ? context.Types.Special(SpecialType.System_Object) : value.Type;
            }
            var typeArguments = markup.Arguments.FirstOrDefault(argument => argument.Name != null &&
                scope.Expand(argument.Name, true) is { LocalName: "TypeArguments", Namespace: { } ns } && XamlNames.IsLanguage(ns))?.Value;
            var extension = context.ResolveType(markup.Name, scope, markup.Span, typeArguments, report: false, extension: true);
            return extension == null ? context.Types.Special(SpecialType.System_Object) : context.Types.MarkupExtensionMethod(extension)?.ReturnType ?? extension;
        }
        if (syntax is XamlElementSyntax element)
        {
            var nested = scope.Push(element);
            var name = nested.Expand(element.Name);
            if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
            {
                if (name.LocalName is "Type" or "Static" or "Reference")
                {
                    var value = new IntrinsicMarkupBinder(context, report: false).BindObject(element, context.Types.Special(SpecialType.System_Object), nested);
                    return (value is BoundReferenceExpression reference ? context.FindName(nameScope, reference.Name) ?? value.Type : value?.Type)
                        ?? context.Types.Special(SpecialType.System_Object);
                }
                if (name.LocalName == "Array")
                {
                    var text = element.Attributes.FirstOrDefault(attribute => attribute.Name == "Type")?.Value;
                    ITypeSymbol? item = null;
                    if (text?.StartsWith("{", StringComparison.Ordinal) == true)
                    {
                        var markup = MarkupExtensionParser.Parse(text, element.Span, _ => { });
                        if (markup != null && nested.Expand(markup.Name) is { LocalName: "Type", Namespace: { } ns } && XamlNames.IsLanguage(ns))
                            item = (new IntrinsicMarkupBinder(context, report: false).Bind(markup, context.Types.Find(ClrNames.Type)!, nested) as BoundTypeExpression)?.ReferencedType;
                    }
                    else if (text != null) item = context.Values.ResolveTypeLiteral(text, nested, element.Span, report: false);
                    return item == null ? null : context.Types.Compilation.CreateArrayTypeSymbol(item);
                }
            }
        }
        var type = context.Values.PeekNodeType(syntax, scope);
        return type == null ? null : context.Types.MarkupExtensionMethod(type)?.ReturnType ?? type;
    }
}
