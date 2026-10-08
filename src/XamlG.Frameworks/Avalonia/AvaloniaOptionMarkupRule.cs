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
        "Options", "Content") { AllowRepeatedAssignments = true, UseFirstMatchingPredicate = true });

    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.LocalName.IndexOf('.') >= 0) return false;
        var scope = parentScope.Push(syntax);
        var typeArguments = scope.Directive(syntax, "TypeArguments");
        var type = context.ResolveTypeAtSource(syntax.Name, scope, syntax.NameSpan, typeArguments?.Value, report: false, typeArgumentSpan: typeArguments?.ValueSpan);
        return type != null && Binder.TryBind(context, syntax, type, scope, nameScope,
            Expected(context, targetType), ImmutableArray<XamlSyntaxNode>.Empty, out expression);
    }

    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var typeArguments = syntax.Arguments.FirstOrDefault(a => a.Name != null &&
            scope.Expand(a.Name, true).LocalName == "TypeArguments");
        var type = context.ResolveTypeAtSource(syntax.Name, scope, syntax.NameSpan ?? syntax.Span, typeArguments?.Value, report: false, extension: true,
            typeArgumentSpan: typeArguments?.ValueSpan ?? typeArguments?.Span);
        if (type == null) return false;
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a =>
            new XamlAttributeSyntax(a.Name!, a.Value, a.NameSpan ?? a.Span, a.ValueSpan ?? a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a =>
            (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.ValueSpan ?? a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.NameSpan ?? syntax.Span, syntax.Span, new(syntax.Span.End, 0),
            attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        return Binder.TryBind(context, element, type, scope, context.Ancestors.FirstOrDefault()?.NameScopeId ?? 0,
            Expected(context, targetType), positional, out expression);
    }

    private static ITypeSymbol Expected(BindingContext context, ITypeSymbol target) =>
        target.SpecialType == SpecialType.System_Object && context.Ancestors.FirstOrDefault() is { } owner
            ? AvaloniaBindingTargetScope.Get(owner) ?? target : target;
}
