using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>Framework-specific attribute semantics run before portable CLR member binding.</summary>
public interface IXamlBindingRule
{
    bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope);
}
