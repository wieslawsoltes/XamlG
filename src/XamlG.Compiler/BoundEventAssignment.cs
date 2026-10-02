using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundEventAssignment(BoundMember Event, string HandlerName, IMethodSymbol? Handler, TextSpan SourceSpan) : BoundAssignment(SourceSpan);
