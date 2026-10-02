using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundSymbolInfo(TextSpan Span, ISymbol Symbol, string Role);
