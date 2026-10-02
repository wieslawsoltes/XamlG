using Microsoft.CodeAnalysis;
namespace XamlG.Compiler;
/// <summary>A statically resolved candidate in an ordered runtime value dispatch.</summary>
public abstract record BoundValueSetter(ITypeSymbol ValueType, bool AllowRuntimeNull);
