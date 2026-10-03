using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record BoundBindingSource(BoundExpression Builder, ITypeSymbol? SourceType, INamedTypeSymbol? DataType);
