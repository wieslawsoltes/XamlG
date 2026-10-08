using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Syntax;
namespace XamlG.Compiler;
public sealed record BoundCallExpression(IMethodSymbol Method, BoundExpression? Receiver, ImmutableArray<BoundExpression> Arguments, TextSpan SourceSpan) : BoundExpression(Method.ReturnType, SourceSpan)
{
    /// <summary>A statically known runtime dispatch type whose public methods this call requires.
    /// Used for referenced libraries with generated metadata names that C# cannot spell.</summary>
    public BoundRuntimeTypeDependency? RuntimeDependency { get; init; }
}

public sealed record BoundRuntimeTypeDependency(string AssemblyName, string MetadataName);
