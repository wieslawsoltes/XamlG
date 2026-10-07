using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundMember(string Name, BoundMemberKind Kind, ISymbol Symbol, ITypeSymbol ValueType,
    IMethodSymbol? Getter, IMethodSymbol? Setter, TextSpan Span)
{
    public BoundExpression? TargetDescriptor { get; init; }
    public ISymbol ConversionSource => Kind == BoundMemberKind.AttachedProperty && Getter != null ? Getter : Symbol;
    public bool CanWrite => Kind is BoundMemberKind.Event or BoundMemberKind.AttachedEvent || Setter != null;
}
