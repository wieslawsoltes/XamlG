using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Uses the public value-provider contract in both markup and element syntax,
/// including bindings nested in MultiBinding collections. Captured services belong to
/// the current construction/template scope, never a process-global binding cache.</summary>
public sealed class AvaloniaBindingMarkupRule : IXamlMarkupBindingRule, IXamlObjectExpressionRule
{
    private readonly AvaloniaCompiledBindingRule _compiled = new();

    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.LocalName.IndexOf('.') >= 0) return false;
        // Direct property bindings already take this route. Collection elements must
        // obey the same policy: a failed compiled path is never retried as reflection.
        if (_compiled.TryBindElement(context, syntax, targetType, parentScope, out expression)) return true;
        var scope = parentScope.Push(syntax);
        var type = context.ResolveType(syntax.Name, scope, syntax.NameSpan,
            scope.Directive(syntax, "TypeArguments")?.Value, report: false);
        if (type == null || !IsReflectionValue(context, type)) return false;
        var extension = ReflectionProvider(context, syntax.Span);
        if (extension == null) return true;
        var bound = context.Objects.Bind(syntax, parentScope, extension, false, nameScope);
        if (bound != null) expression = Provide(context, bound, targetType, syntax.Span);
        return true;
    }

    public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
        NamespaceScope scope, out BoundExpression? expression)
    {
        expression = null;
        var binding = context.Types.Find(AvaloniaMetadata.BindingBase);
        if (binding == null) return false;
        var type = context.ResolveType(syntax.Name, scope, syntax.Span, report: false, extension: true);
        if (type == null || !context.Types.Compilation.ClassifyCommonConversion(type, binding).IsImplicit) return false;
        if (IsReflectionValue(context, type))
        {
            type = ReflectionProvider(context, syntax.Span);
            if (type == null) return true;
        }
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a =>
            new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a =>
            (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0),
            attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        var nameScope = context.Ancestors.FirstOrDefault()?.NameScopeId ?? 0;
        var bound = context.Objects.Bind(element, scope, type, false, nameScope, positional);
        if (bound != null) expression = Provide(context, bound, targetType, syntax.Span);
        return true;
    }

    private static bool IsReflectionValue(BindingContext context, INamedTypeSymbol type)
    {
        if (type.HasMetadataName(AvaloniaBindingMetadata.ReflectionBinding)) return true;
        // Resolve the actual base contract from the referenced framework version.
        // Exact identity avoids substituting user subclasses or compiled bindings.
        var extension = context.Types.Find(AvaloniaBindingMetadata.ReflectionExtension);
        return extension?.BaseType is { } value && SymbolEqualityComparer.Default.Equals(type, value);
    }

    private static INamedTypeSymbol? ReflectionProvider(BindingContext context, TextSpan span)
    {
        var extension = context.Types.Find(AvaloniaBindingMetadata.ReflectionExtension);
        if (extension == null)
            context.Report("XG3003", "The reflection binding value-provider contract is unavailable.", span);
        return extension;
    }

    private static BoundExpression Provide(BindingContext context, BoundObject bound, ITypeSymbol targetType, TextSpan span)
    {
        var provide = bound.Type.Members(context.Types.Configuration.MarkupExtensionMethod).OfType<IMethodSymbol>()
            .Where(method => !method.IsStatic && !method.IsGenericMethod && !method.ReturnsVoid &&
                context.Types.IsAccessible(method) && (method.Parameters.Length == 0 ||
                method.Parameters.Length == 1 && method.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider)))
            .OrderBy(method => method.ReturnType.SpecialType == SpecialType.System_Object ? 1 : 0)
            .ThenBy(method => context.Types.Compilation.ClassifyCommonConversion(method.ReturnType, targetType).IsImplicit ? 0 : 1)
            .ThenBy(method => method.Parameters.Length).FirstOrDefault();
        return provide == null ? new BoundObjectExpression(bound)
            : new BoundMarkupExpression(bound, provide, provide.ReturnType, span);
    }
}
