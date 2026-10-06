using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Constructs text-initialized objects through the same symbol-resolved conversion
/// pipeline as property literals. Only literal-only elements qualify; no members are dropped.</summary>
public sealed class XamlTextObjectExpressionRule : IXamlObjectExpressionRule
{
    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.Children.Any(node => node is XamlElementSyntax) || !syntax.Children.OfType<XamlTextSyntax>().Any()) return false;
        var scope = parentScope.Push(syntax);
        foreach (var attribute in syntax.Attributes)
        {
            if (attribute.IsNamespace) continue;
            var name = scope.Expand(attribute.Name, true);
            if (name.Namespace == XamlNames.Xml && name.LocalName == "space") continue;
            if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName is "Key" or "TypeArguments") continue;
            return false;
        }
        var type = context.ResolveType(syntax.Name, scope, syntax.NameSpan, scope.Directive(syntax, "TypeArguments")?.Value, report: false);
        if (type == null) return false;
        var text = string.Concat(syntax.Children.OfType<XamlTextSyntax>().Select(node => node.Value));
        if (type.SpecialType != SpecialType.System_String && string.IsNullOrWhiteSpace(text)) return false;
        expression = context.Values.TryText(type.SpecialType == SpecialType.System_String ? text : XmlWhitespace.Normalize(text, scope.PreserveSpace),
            type, scope, syntax.Span);
        return expression != null;
    }
}
