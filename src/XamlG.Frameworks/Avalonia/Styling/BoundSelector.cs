using Microsoft.CodeAnalysis;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>The selected element and its last template owner are distinct semantic types.
/// HasTemplateScope also represents an ambiguous or untyped template traversal: callers
/// must not replace that unknown owner with an unrelated enclosing theme.</summary>
internal sealed record BoundSelector(BoundExpression Expression, INamedTypeSymbol? TargetType,
    INamedTypeSymbol? TemplateOwnerType = null, bool HasTemplateScope = false);
