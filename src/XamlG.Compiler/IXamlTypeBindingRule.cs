using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
public interface IXamlTypeBindingRule
{
    bool TryResolve(BindingContext context, XamlTypeNameSyntax syntax, NamespaceScope scope, out INamedTypeSymbol? type);
}
