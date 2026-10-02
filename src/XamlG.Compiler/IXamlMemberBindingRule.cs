using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
namespace XamlG.Compiler;
/// <summary>Decorates an already resolved member with framework-specific semantic information.</summary>
public interface IXamlMemberBindingRule
{
    BoundMember Bind(BindingContext context, ITypeSymbol targetType, BoundMember member, NamespaceScope scope);
}
