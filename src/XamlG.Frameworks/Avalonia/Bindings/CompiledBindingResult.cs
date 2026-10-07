using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record CompiledBindingResult(BoundExpression Expression, ITypeSymbol? ValueType);
