using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundSourceInfo(IMethodSymbol Constructor, IMethodSymbol ObjectSetter)
{
    public BoundNewExpression CreateValue(XamlSyntaxTree syntax, TextSpan span)
    {
        var position = syntax.Lines.GetPosition(span.Start);
        return new(Constructor, ImmutableArray.Create<BoundExpression>(
            new BoundConstantExpression(position.Line + 1, Constructor.Parameters[0].Type, span),
            new BoundConstantExpression(position.Character + 1, Constructor.Parameters[1].Type, span),
            new BoundConstantExpression(syntax.Path.Length == 0 ? null : syntax.Path, Constructor.Parameters[2].Type, span)), span);
    }
}
