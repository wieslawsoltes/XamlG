using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class BindingSourceResolver(BindingContext context, ObjectBindingBuilder target)
{
    public (INamedTypeSymbol? Type, INamedTypeSymbol? DataType) Named(string name, TextSpan span)
    {
        var syntax = context.Syntax.Root;
        if (syntax == null) return (null, null);
        var scopeRoot = context.Ancestors.Where(a => a.NameScopeId == target.NameScopeId).LastOrDefault()?.Syntax ?? syntax;
        var found = scopeRoot.DescendantsAndSelf().FirstOrDefault(e => Scope(e).Directive(e, "Name")?.Value == name);
        if (found == null) { context.Report("XG3203", "Named binding source was not found: " + name, span); return (null, null); }
        var scope = Scope(found);
        var type = context.ResolveType(found.Name, scope, span, scope.Directive(found, "TypeArguments")?.Value);
        return (type, DataType(found));
    }

    public (INamedTypeSymbol? Type, INamedTypeSymbol? DataType, int Level) Parent(BindingPathSegment segment, NamespaceScope scope)
    {
        INamedTypeSymbol? type = null; var level = 0;
        if (segment.Arguments.Length > 2) { context.Report("XG3204", "A parent source accepts a type and optional level.", segment.Span); return (null, null, 0); }
        if (!segment.Arguments.IsEmpty)
        {
            if (int.TryParse(segment.Arguments[0], out var numeric)) level = numeric;
            else type = context.ResolveType(segment.Arguments[0], scope, segment.Span);
            if (segment.Arguments.Length == 2 && !int.TryParse(segment.Arguments[1], out level)) level = -1;
            if (level < 0) { context.Report("XG3204", "The parent level must be non-negative.", segment.Span); return (null, null, 0); }
        }
        var styled = context.Types.Find(AvaloniaStyleMetadata.StyledElement)!;
        var candidates = context.Ancestors.Where(a => !ReferenceEquals(a, target) && context.Types.Compilation.ClassifyCommonConversion(a.Type, styled).IsImplicit)
            .Where(a => type == null || context.Types.Compilation.ClassifyCommonConversion(a.Type, type).IsImplicit).ToArray();
        var parent = candidates.ElementAtOrDefault(level);
        type ??= parent?.Type ?? context.Types.Find(AvaloniaMetadata.Control);
        var data = parent != null && parent.Annotations.TryGet(AvaloniaBindingScope.Key, out var known) ? known.DataType : null;
        return (type, data, level);
    }

    public NamespaceScope Scope(XamlElementSyntax element)
    {
        var scope = NamespaceScope.Empty;
        foreach (var ancestor in context.Syntax.Root!.DescendantsAndSelf().Where(e => e.Span.Contains(element.Span)).OrderByDescending(e => e.Span.Length))
            scope = scope.Push(ancestor);
        return scope;
    }
    private INamedTypeSymbol? DataType(XamlElementSyntax element)
    {
        foreach (var ancestor in context.Syntax.Root!.DescendantsAndSelf().Where(e => e.Span.Contains(element.Span)).OrderBy(e => e.Span.Length))
        {
            var scope = Scope(ancestor);
            var declared = scope.Directive(ancestor, AvaloniaBindingMetadata.DataType);
            if (declared != null) return AvaloniaBindingScopeRule.ResolveDataType(context, declared.Value, scope, declared.ValueSpan);
        }
        return null;
    }
}
