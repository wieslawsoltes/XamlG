using Microsoft.CodeAnalysis;

namespace XamlG.Frameworks.Avalonia.Styling;

internal sealed record AvaloniaSetterScope(INamedTypeSymbol? TargetType, bool HasComplexActivator);
