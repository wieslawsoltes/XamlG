using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundSourceInfo(IMethodSymbol Constructor, IMethodSymbol ObjectSetter)
{
    public static TextSpan ValueLocation(XamlSyntaxTree syntax, TextSpan span)
    {
        var element = syntax.FindElement(span.Start);
        var attribute = element?.Attributes.FirstOrDefault(item => item.ValueSpan.Start <= span.Start && span.Start < item.ValueSpan.End);
        if (attribute != null) return attribute.NameSpan;
        if (element?.Children.OfType<XamlTextSyntax>().FirstOrDefault() is { } text)
            return text.IsCData ? new(text.Span.Start + 9, 0) : text.Span;
        return element?.Children.OfType<XamlElementSyntax>().FirstOrDefault()?.NameSpan ?? element?.NameSpan ?? span;
    }

    public BoundNewExpression CreateValue(XamlSyntaxTree syntax, TextSpan span)
    {
        var position = syntax.Lines.GetPosition(span.Start);
        return new(Constructor, ImmutableArray.Create<BoundExpression>(
            new BoundConstantExpression(position.Line + 1, Constructor.Parameters[0].Type, span),
            new BoundConstantExpression(position.Character + 1, Constructor.Parameters[1].Type, span),
            new BoundConstantExpression(syntax.Path.Length == 0 ? null : syntax.Path, Constructor.Parameters[2].Type, span)), span);
    }
}
