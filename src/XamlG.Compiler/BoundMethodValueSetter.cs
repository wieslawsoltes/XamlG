using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
namespace XamlG.Compiler;
/// <summary>Calls a method on the target, a statically resolved property chain, or a static adapter.</summary>
public sealed record BoundMethodValueSetter(IMethodSymbol Method, ImmutableArray<IPropertySymbol> ReceiverPath, bool IncludeTarget = false)
    : BoundValueSetter(Method.Parameters.Last().Type, Method.Parameters.Last().Type.AcceptsNull());
