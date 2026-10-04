using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Replaces a framework object element with a typed expression before constructors or members are bound.</summary>
public interface IXamlObjectExpressionRule
{
    bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression);
}
