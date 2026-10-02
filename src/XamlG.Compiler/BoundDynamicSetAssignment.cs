using System.Collections.Immutable;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundDynamicSetAssignment(BoundMember Target, BoundExpression Value, ImmutableArray<BoundValueSetter> Candidates, TextSpan SourceSpan)
    : BoundAssignment(SourceSpan);
