using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record BoundSelector(BoundExpression Expression, INamedTypeSymbol? TargetType);
