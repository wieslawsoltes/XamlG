using Microsoft.CodeAnalysis;
using XamlG.Roslyn;

namespace XamlG.Compiler;

/// <summary>Reads a collection only after its adder wins runtime value dispatch.</summary>
public sealed record BoundCollectionValueSetter(BoundMember Collection, IMethodSymbol AddMethod)
    : BoundValueSetter(AddMethod.Parameters.Last().Type, AddMethod.Parameters.Last().Type.AcceptsNull());
