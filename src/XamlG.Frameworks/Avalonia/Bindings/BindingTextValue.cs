using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record BindingTextValue(string Text, TextSpan Span, NamespaceScope? Scope = null);
