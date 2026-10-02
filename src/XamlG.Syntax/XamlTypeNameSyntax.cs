using System.Collections.Immutable;
namespace XamlG.Syntax;
public sealed record XamlTypeNameSyntax(string Name, ImmutableArray<XamlTypeNameSyntax> Arguments, bool Nullable, TextSpan Span);
