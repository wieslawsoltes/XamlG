using System.Collections.Immutable;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
/// <summary>Intercepts a resolved property's value while preserving normal type/member resolution and diagnostics.</summary>
public interface IXamlPropertyBindingRule
{
    bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute);
}
