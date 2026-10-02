using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundRawAssignment(string CSharp, TextSpan SourceSpan) : BoundAssignment(SourceSpan);
