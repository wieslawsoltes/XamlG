using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundStaticExpression(ISymbol Member, ITypeSymbol ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan)
{
    /// <summary>A framework-declared member produced by another source generator; Member retains its source declaration.</summary>
    public string? GeneratedMemberName { get; init; }
}
