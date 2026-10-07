using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundDeferredExpression(BoundExpression Content, ITypeSymbol TargetType, TextSpan SourceSpan) : BoundExpression(TargetType, SourceSpan)
{
    public IMethodSymbol? Customizer { get; init; }
    public ITypeSymbol? FactoryReturnType { get; init; }
    public int? NameScopeId { get; init; }
    public bool UsesFunctionPointer => Customizer?.Parameters[0].Type.SpecialType == SpecialType.System_IntPtr;
}
