using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>A framework-owned immutable reference whose equivalent construction can be
/// shared within the generated document or project. The key identifies the complete value, including
/// closed generic types and index arguments. Construction must not depend on a runtime frame.
/// Concurrent first reads may construct equivalent instances, as with a lazy framework cache.</summary>
public sealed record BoundCachedExpression(string Key, BoundExpression Value, TextSpan SourceSpan)
    : BoundExpression(Value.Type, SourceSpan)
{
    /// <summary>The expression uses only closed types and assembly-accessible members,
    /// and carries no document-specific services or source metadata.</summary>
    public bool ShareAcrossDocuments { get; init; }
}
