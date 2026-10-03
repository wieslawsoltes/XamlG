using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Compiler;

public sealed record BoundFieldAccessExpression(BoundExpression Receiver, IFieldSymbol Field, TextSpan SourceSpan)
    : BoundExpression(Field.Type, SourceSpan);
