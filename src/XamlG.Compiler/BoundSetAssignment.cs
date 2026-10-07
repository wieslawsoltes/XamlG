using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundSetAssignment(BoundMember Member, BoundExpression Value, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    /// <summary>Registers the assigned string in the configured namescopes after the setter
    /// completes, preserving the original value without evaluating it a second time.</summary>
    public bool RegisterName { get; init; }
}
