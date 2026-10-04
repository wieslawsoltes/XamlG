using XamlG.Syntax;

namespace XamlG.Compiler.Resources;

/// <summary>A direct call to a compiled factory, carrying the caller's construction services.</summary>
public sealed record BoundResourceExpression(XamlResourceDescriptor Resource, TextSpan SourceSpan)
    : BoundExpression(Resource.RootType, SourceSpan);
