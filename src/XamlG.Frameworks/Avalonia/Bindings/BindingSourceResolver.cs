using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal sealed class BindingSourceResolver(BindingContext context, ObjectBindingBuilder target)
{
    public (INamedTypeSymbol? Type, ITypeSymbol? DataType) Named(string name, TextSpan span)
    {
        var found = AvaloniaNamedSourceScopes.Get(context).Find(name);
        if (found.Type == null) context.Report("XG3203", "Named binding source was not found: " + name, span);
        return found;
    }

    public (INamedTypeSymbol? Type, ITypeSymbol? DataType, int Level) Parent(BindingPathSegment segment, NamespaceScope scope)
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

}
