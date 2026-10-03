using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Normalizes markup-extension and object-element binding forms before semantic binding.</summary>
internal sealed record CompiledBindingInput(
    XamlElementSyntax ObjectSyntax,
    NamespaceScope ParentScope,
    NamespaceScope Scope,
    BindingTextValue Path,
    BindingTextValue? DataType,
    BindingTextValue? ElementName,
    BindingRelativeSourceInput? RelativeSource);
