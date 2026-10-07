using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public abstract record BoundExpression(ITypeSymbol? Type, TextSpan Span)
{
    /// <summary>Framework value location, independent of the diagnostic span. Constructors
    /// with an explicit location opt into configured runtime source metadata.</summary>
    public TextSpan? SourceInfoSpan { get; init; }
}
