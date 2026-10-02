using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
/// <summary>Trusted profile-generated code only. Never create this node from unvalidated XAML text.</summary>
public sealed record BoundRawExpression(string CSharp, ITypeSymbol ValueType, TextSpan SourceSpan) : BoundExpression(ValueType, SourceSpan);
