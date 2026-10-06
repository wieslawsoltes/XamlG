using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Bindings without ProvideValue remain first-class BindingBase objects.
/// Extensions must still execute their public value provider to capture namescope,
/// anchor and type-resolution services before the resulting binding is attached.</summary>
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
        // A known framework Binding value used in markup syntax requires the same
        // public service capture as the explicit ReflectionBinding extension.
        if (type.HasMetadataName(AvaloniaBindingMetadata.ReflectionBinding))
        {
            var extension = context.Types.Find(AvaloniaBindingMetadata.ReflectionExtension);
            if (extension == null)
            {
                context.Report("XG3003", "The reflection binding value-provider contract is unavailable.", syntax.Span);
                return true;
            }
            type = extension;
        }
        var attributes = syntax.Arguments.Where(a => a.Name != null).Select(a =>
            new XamlAttributeSyntax(a.Name!, a.Value, a.Span, a.Span, a.Span, '"')).ToImmutableArray();
        var positional = syntax.Arguments.Where(a => a.Name == null).Select(a =>
            (XamlSyntaxNode)new XamlTextSyntax(a.Value, false, a.Span)).ToImmutableArray();
        var element = new XamlElementSyntax(syntax.Name, syntax.Span, syntax.Span, new(syntax.Span.End, 0),
            attributes, ImmutableArray<XamlSyntaxNode>.Empty, true, syntax.Span);
        var nameScope = context.Ancestors.Count == 0 ? 0 : context.Ancestors.Peek().NameScopeId;
        var bound = context.Objects.Bind(element, scope, type, false, nameScope, positional);
        if (bound == null) return true;
        var provide = type.Members(context.Types.Configuration.MarkupExtensionMethod).OfType<IMethodSymbol>()
            .Where(method => !method.IsStatic && !method.IsGenericMethod && !method.ReturnsVoid &&
                context.Types.IsAccessible(method) && (method.Parameters.Length == 0 ||
                method.Parameters.Length == 1 && method.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider)))
            .OrderBy(method => method.ReturnType.SpecialType == SpecialType.System_Object ? 1 : 0)
            .ThenBy(method => context.Types.Compilation.ClassifyCommonConversion(method.ReturnType, targetType).IsImplicit ? 0 : 1)
            .ThenBy(method => method.Parameters.Length).FirstOrDefault();
        expression = provide == null ? new BoundObjectExpression(bound)
            : new BoundMarkupExpression(bound, provide, provide.ReturnType, syntax.Span);
        return true;
    }
}
