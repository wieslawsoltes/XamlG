using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
/// <summary>Framework markup semantics such as compiled paths; the portable markup binder remains available.</summary>
public interface IXamlMarkupBindingRule
{
    bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType, NamespaceScope scope,
        out BoundExpression? expression);
}
