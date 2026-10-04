using Microsoft.CodeAnalysis;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

public sealed record XamlNameOccurrence(string Name, int NameScopeId, TextSpan Span, bool IsDeclaration, ITypeSymbol? Type);
