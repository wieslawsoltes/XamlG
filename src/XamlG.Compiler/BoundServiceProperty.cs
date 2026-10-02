using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

public sealed record BoundServiceProperty(IPropertySymbol Property, XamlServiceValue Value);
