using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Preserves qualified property elements when a binding value is lowered to
/// its service-capturing provider. Rewriting is limited to resolved compatible members;
/// foreign owners, namespace bindings, value children and original source spans survive.</summary>
internal static class BindingPropertyOwnerRewriter
{
    private const string PrefixStem = "_xamlgBinding";

    public static XamlElementSyntax Rewrite(BindingContext context, XamlElementSyntax syntax,
        NamespaceScope scope, INamedTypeSymbol originalType, INamedTypeSymbol providerType)
    {
        var children = ImmutableArray.CreateBuilder<XamlSyntaxNode>(syntax.Children.Length);
        var changed = false;
        foreach (var child in syntax.Children)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (child is not XamlElementSyntax property || property.LocalName.IndexOf('.') < 0)
            { children.Add(child); continue; }
            var propertyScope = scope.Push(property);
            var original = context.Members.Resolve(originalType, property.Name, propertyScope, property.NameSpan, report: false);
            var replacement = original?.Kind == BoundMemberKind.Property
                ? context.Members.Resolve(providerType, original.Name, propertyScope, property.NameSpan, report: false) : null;
            if (original == null || replacement?.Kind != BoundMemberKind.Property ||
                !SymbolEqualityComparer.Default.Equals(original.ValueType, replacement.ValueType))
            { children.Add(child); continue; }
            // A fresh local namespace avoids assuming that the source prefix also
            // contains the replacement type (e.g. using:Avalonia.Data does not).
            var prefix = PrefixStem;
            var suffix = 0;
            while (propertyScope.Bindings.ContainsKey(prefix))
                prefix = PrefixStem + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var uri = "clr-namespace:" + providerType.ContainingNamespace.ToDisplayString() +
                ";assembly=" + providerType.ContainingAssembly.Name;
            var generatedSpan = new TextSpan(property.NameSpan.End, 0);
            var declaration = new XamlAttributeSyntax("xmlns:" + prefix, uri,
                generatedSpan, generatedSpan, generatedSpan, '"');
            children.Add(property with
            {
                Name = prefix + ":" + providerType.Name + "." + replacement.Name,
                Attributes = property.Attributes.Add(declaration)
            });
            changed = true;
        }
        return changed ? syntax with { Children = children.ToImmutable() } : syntax;
    }
}
