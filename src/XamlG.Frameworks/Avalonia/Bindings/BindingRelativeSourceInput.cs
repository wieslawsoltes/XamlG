using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record BindingRelativeSourceInput(MarkupExtensionSyntax Syntax, NamespaceScope Scope);
