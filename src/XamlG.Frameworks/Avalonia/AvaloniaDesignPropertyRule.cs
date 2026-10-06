using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Production compilation excludes framework Design attached values, matching
/// Avalonia's non-design compiler policy. The original syntax remains available to tooling.</summary>
public sealed class AvaloniaDesignPropertyRule : IXamlPropertyBindingRule
{
    public bool TryBind(BindingContext context, ObjectBindingBuilder target, BoundMember member,
        ImmutableArray<XamlSyntaxNode> values, NamespaceScope scope, TextSpan span, bool isAttribute) =>
        member.Kind == BoundMemberKind.AttachedProperty && member.Symbol.ContainingType?.HasMetadataName(AvaloniaLiteralMetadata.Design) == true;
}
