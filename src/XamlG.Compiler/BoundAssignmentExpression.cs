using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundAssignmentExpression(BoundExpression Target, BoundExpression Value, TextSpan SourceSpan)
    : BoundExpression(Target.Type, SourceSpan);
