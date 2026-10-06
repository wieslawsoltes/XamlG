using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using XamlG.CSharp.Resources;

namespace XamlG.CSharp.Integration;

internal sealed record LoaderCall(InvocationExpressionSyntax Syntax, IMethodSymbol Method,
    string AttributeSource, LoaderCallKind Kind, bool HasServices, XamlProjectDocumentResult? StaticTarget);
