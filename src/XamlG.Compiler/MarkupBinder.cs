using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

internal sealed class MarkupBinder
{
    private readonly BindingContext _context;
    public MarkupBinder(BindingContext context) => _context = context;
    public BoundExpression? Bind(MarkupExtensionSyntax syntax, ITypeSymbol target, NamespaceScope scope)
    {
        foreach (var rule in _context.Profile.MarkupBindingRules)
            if (rule.TryBind(_context, syntax, target, scope, out var value)) return value;
        var name = scope.Expand(syntax.Name);
        if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
            return new IntrinsicMarkupBinder(_context).Bind(syntax, target, scope);
        var genericArguments = syntax.Arguments.FirstOrDefault(a => a.Name != null && scope.Expand(a.Name, true).LocalName == "TypeArguments" && scope.Expand(a.Name, true).Namespace is string ns && XamlNames.IsLanguage(ns))?.Value;
        var typeSymbol = _context.ResolveType(syntax.Name, scope, syntax.Span, genericArguments, extension: true);
        if (typeSymbol == null) return null;
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a => new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a => (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0), attributes,
            ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        var obj = _context.Objects.Bind(element, scope, typeSymbol, false, _context.Ancestors.Count == 0 ? 0 : _context.Ancestors.Peek().NameScopeId, positional);
        if (obj == null) return null;
        return Provide(obj, syntax.Span) ?? Missing($"'{typeSymbol}' does not have a supported markup-extension provider method.", syntax.Span);
    }
    public BoundExpression? Provide(BoundObject obj, TextSpan span)
    {
        var method = _context.Types.MarkupExtensionMethod(obj.Type);
        return method == null ? null : new BoundMarkupExpression(obj, method, method.ReturnType, span);
    }
    public BoundExpression? Static(string text, ITypeSymbol target, NamespaceScope scope, TextSpan span, string? typeArguments = null, NamespaceScope? typeArgumentScope = null)
    {
        text = text.Trim();
        var dot = text.LastIndexOf('.');
        if (dot < 1) return Missing("x:Static requires Type.Member.", span);
        var owner = _context.ResolveType(text.Substring(0, dot), scope, span, typeArguments, typeArgumentScope: typeArgumentScope);
        if (owner == null) return null;
        var member = owner.Members(text.Substring(dot + 1)).FirstOrDefault(m => m.IsStatic && _context.Types.IsAccessible(m) &&
            (m is IFieldSymbol || m is IPropertySymbol p && p.GetMethod != null && _context.Types.IsAccessible(p.GetMethod)));
        if (member == null) return Missing($"Static member '{text}' was not found or is inaccessible.", span);
        _context.Symbols.Add(new(span, member, "static"));
        var type = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
        return _context.Values.Coerce(new BoundStaticExpression(member, type, span), target, span);
    }
    private BoundExpression? Missing(string message, TextSpan span) { _context.Report("XG1009", message, span); return null; }
}
