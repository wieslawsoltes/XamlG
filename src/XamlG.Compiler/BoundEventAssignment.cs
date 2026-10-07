using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundEventAssignment(BoundMember Event, string HandlerName, IMethodSymbol? Handler, TextSpan SourceSpan) : BoundAssignment(SourceSpan)
{
    /// <summary>An explicitly bound delegate value; null retains named root-handler binding.</summary>
    public BoundExpression? Value { get; init; }
}
