using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Resources;

public sealed class AvaloniaResourceIncludeRule : IXamlObjectExpressionRule
{
    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        var scope = parentScope.Push(syntax);
        var type = context.ResolveType(syntax.Name, scope, syntax.NameSpan, report: false);
        if (type == null) return false;
        if (type.HasMetadataName(AvaloniaResourceMetadata.MergeResourceInclude))
        { context.Report("XG3303", "MergeResourceInclude is valid only inside ResourceDictionary.MergedDictionaries.", syntax.NameSpan); return true; }
        if (!type.HasMetadataName(AvaloniaResourceMetadata.ResourceInclude) && !type.HasMetadataName(AvaloniaResourceMetadata.StyleInclude)) return false;
        expression = Resolve(context, syntax, parentScope, type.HasMetadataName(AvaloniaResourceMetadata.StyleInclude));
        return true;
    }
    internal static BoundResourceExpression? Resolve(BindingContext context, XamlElementSyntax syntax, NamespaceScope parentScope, bool style)
    {
        var scope = parentScope.Push(syntax);
        var sources = new List<(string Value, TextSpan Span)>();
        foreach (var attribute in syntax.Attributes.Where(a => !a.IsNamespace))
        {
            var expanded = scope.Expand(attribute.Name, true);
            if (attribute.Name == AvaloniaResourceMetadata.Source) sources.Add((attribute.Value, attribute.ValueSpan));
            else if (expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName == "Key") { }
            else { context.Report("XG3302", "Unsupported compiled-include attribute: " + attribute.Name, attribute.NameSpan); return null; }
        }
        foreach (var child in syntax.Children)
        {
            if (child is XamlTriviaSyntax || child is XamlTextSyntax text && string.IsNullOrWhiteSpace(text.Value)) continue;
            if (child is XamlElementSyntax property && property.LocalName.EndsWith("." + AvaloniaResourceMetadata.Source, StringComparison.Ordinal) && !property.Children.OfType<XamlElementSyntax>().Any())
                sources.Add((string.Concat(property.Children.OfType<XamlTextSyntax>().Select(t => t.Value)).Trim(), property.Span));
            else { context.Report("XG3302", "A compiled include accepts only a static Source.", child.Span); return null; }
        }
        if (sources.Count != 1 || string.IsNullOrWhiteSpace(sources[0].Value) || sources[0].Value.StartsWith("{", StringComparison.Ordinal))
        { context.Report("XG3302", "A compiled include requires exactly one static Source URI.", syntax.NameSpan); return null; }
        if (context.Options.Resources == null)
        { context.Report("XG3301", "Includes require a project resource catalog. Compile the document set with XamlProjectCompiler.", sources[0].Span); return null; }
        var lookup = context.Options.Resources.Resolve(context.Options.ResourceUri ?? context.Options.BaseUri, sources[0].Value);
        if (!lookup.Success)
        { context.Report("XG3301", lookup.Error!, sources[0].Span); return null; }
        var expected = context.Types.Find(style ? AvaloniaResourceMetadata.Style : AvaloniaResourceMetadata.Dictionary);
        if (expected == null || !context.Types.Compilation.ClassifyCommonConversion(lookup.Resource!.RootType, expected).IsImplicit)
        { context.Report("XG3306", "The include target must be " + (style ? "an IStyle" : "a ResourceDictionary") + ", not '" + lookup.Resource!.RootType.ToDisplayString() + "'.", sources[0].Span); return null; }
        return new(lookup.Resource!, sources[0].Span);
    }
}
