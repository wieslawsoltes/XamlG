using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Options;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Supplies Avalonia metadata and the eventual property type to the shared
/// options lowering pass. No platform or form-factor names are built into the compiler.</summary>
public sealed class AvaloniaOptionMarkupRule : IXamlObjectExpressionRule, IXamlMarkupBindingRule
{
    private static readonly XamlOptionMarkupBinder Binder = new(new(
        "ShouldProvideOption", "Avalonia.Metadata.MarkupExtensionOptionAttribute",
        "Avalonia.Metadata.MarkupExtensionDefaultOptionAttribute",
        ImmutableArray.Create("Avalonia.Markup.Xaml.MarkupExtensions.On", "Avalonia.Markup.Xaml.MarkupExtensions.On`1"),
        "Options", "Content"));

    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.LocalName.IndexOf('.') >= 0) return false;
        var scope = parentScope.Push(syntax);
        var type = context.ResolveType(syntax.Name, scope, syntax.NameSpan,
            scope.Directive(syntax, "TypeArguments")?.Value, report: false);
        return type != null && Binder.TryBind(context, syntax, type, scope, nameScope,
            Expected(context, targetType), ImmutableArray<XamlSyntaxNode>.Empty, out expression);
    }

    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var typeArguments = syntax.Arguments.FirstOrDefault(a => a.Name != null &&
            scope.Expand(a.Name, true).LocalName == "TypeArguments")?.Value;
        var type = context.ResolveType(syntax.Name, scope, syntax.Span, typeArguments, report: false, extension: true);
        if (type == null) return false;
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a =>
            new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a =>
            (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0),
            attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        return Binder.TryBind(context, element, type, scope, context.Ancestors.FirstOrDefault()?.NameScopeId ?? 0,
            Expected(context, targetType), positional, out expression);
    }

    private static ITypeSymbol Expected(BindingContext context, ITypeSymbol target) =>
        target.SpecialType == SpecialType.System_Object && context.Ancestors.FirstOrDefault() is { } owner
            ? AvaloniaBindingTargetScope.Get(owner) ?? target : target;
}
