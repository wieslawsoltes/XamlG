using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Avalonia 12 bindings are BindingBase objects, not necessarily ProvideValue markup extensions.</summary>
public sealed class AvaloniaBindingMarkupRule : IXamlMarkupBindingRule
{
    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var binding = context.Types.Find(AvaloniaMetadata.BindingBase);
        if (binding == null) return false;
        var type = context.ResolveType(syntax.Name, scope, syntax.Span, report: false, extension: true);
        if (type == null || !context.Types.Compilation.ClassifyCommonConversion(type, binding).IsImplicit) return false;
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a =>
            new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a =>
            (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0),
            attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        var nameScope = context.Ancestors.Count == 0 ? 0 : context.Ancestors.Peek().NameScopeId;
        var bound = context.Objects.Bind(element, scope, type, false, nameScope, positional);
        if (bound != null) expression = new BoundObjectExpression(bound);
        return true;
    }
}
