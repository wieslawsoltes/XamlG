using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed record BindingPropertyValue(string Name, ImmutableArray<XamlSyntaxNode> Values, NamespaceScope Scope, TextSpan NameSpan, TextSpan Span)
{
    public string MemberName => Name.Substring(Name.LastIndexOf('.') + 1);

    public static IEnumerable<BindingPropertyValue> Read(BindingContext context, ObjectBindingBuilder target)
    {
        bool Ignored(NamespaceScope scope, string name, bool attribute)
        {
            var ns = scope.Expand(name, attribute).Namespace;
            return ns != null && (ns == XamlNames.Xml || ns == XamlNames.Compatibility ||
                scope.IgnoredNamespaces.Contains(ns) || context.Types.Configuration.IgnoredNamespaces.Contains(ns));
        }
        foreach (var attribute in target.Syntax.Attributes)
            if (!attribute.IsNamespace && !Ignored(target.Scope, attribute.Name, true))
                yield return new(attribute.Name, ImmutableArray.Create<XamlSyntaxNode>(new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan)),
                    target.Scope, attribute.NameSpan, attribute.Span);
        foreach (var property in target.Syntax.Children.OfType<XamlElementSyntax>())
        {
            var scope = target.Scope.Push(property);
            if (property.LocalName.IndexOf('.') >= 0 && !Ignored(scope, property.Name, false))
                yield return new(property.Name, property.Children, scope, property.NameSpan, property.Span);
        }
    }
}
