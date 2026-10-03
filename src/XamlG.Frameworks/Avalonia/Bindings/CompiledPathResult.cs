using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record CompiledPathResult(BoundExpression Path, ITypeSymbol? ValueType, bool CanWrite);
