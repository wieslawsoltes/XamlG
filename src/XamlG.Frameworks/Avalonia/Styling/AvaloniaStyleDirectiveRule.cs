using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

public sealed class AvaloniaStyleDirectiveRule : IXamlBindingRule
{
    public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target,
        XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        var name = scope.Expand(attribute.Name, true);
        return name.Namespace != null && XamlNames.IsLanguage(name.Namespace) &&
            name.LocalName == AvaloniaStyleMetadata.SetterTargetType;
    }
}
