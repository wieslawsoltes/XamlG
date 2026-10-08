using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Infers a static resource's declared type without constructing or evaluating its value.</summary>
internal static class BindingResourceTypeResolver
{
    public static ITypeSymbol? Find(BindingContext context, BoundExpression source)
    {
        if (source is not BoundMarkupExpression markup ||
            !markup.Extension.Type.HasMetadataName("Avalonia.Markup.Xaml.MarkupExtensions.StaticResourceExtension") ||
            markup.Extension.Arguments.Length != 1 ||
            markup.Extension.Arguments[0] is not BoundConstantExpression { Value: string key }) return null;

        var styledElement = context.Types.Find(AvaloniaStyleMetadata.StyledElement);
        if (styledElement == null) return null;
        foreach (var ancestor in context.Ancestors)
        {
            if (!context.Types.Compilation.ClassifyCommonConversion(ancestor.Type, styledElement).IsImplicit) continue;
            foreach (var property in ancestor.Syntax.Children.OfType<XamlElementSyntax>())
            {
                if (!property.LocalName.EndsWith(".Resources", StringComparison.Ordinal)) continue;
                var member = context.Members.Resolve(ancestor.Type, property.Name, ancestor.Scope, property.NameSpan, report: false);
                if (member?.Symbol.ContainingType.HasMetadataName(AvaloniaStyleMetadata.StyledElement) != true) continue;
                var scope = ancestor.Scope.Push(property);
                var resources = property.Children.OfType<XamlElementSyntax>().ToArray();
                if (resources.Length == 1 && context.Values.PeekNodeType(resources[0], scope)?.HasMetadataName("Avalonia.Controls.ResourceDictionary") == true)
                {
                    scope = scope.Push(resources[0]);
                    resources = resources[0].Children.OfType<XamlElementSyntax>().ToArray();
                }
                foreach (var resource in resources)
                    if (scope.Push(resource).Directive(resource, "Key")?.Value == key)
                        return context.Values.PeekValueType(resource, scope, ancestor.NameScopeId);
            }
        }
        return null;
    }
}
