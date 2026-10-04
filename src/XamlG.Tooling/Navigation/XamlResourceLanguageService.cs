using System.Collections.Immutable;
using XamlG.Compiler.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Tooling.Navigation;

/// <summary>Metadata-only navigation; profiles declare source-bearing include members.</summary>
public sealed class XamlResourceLanguageService(XamlCompilationSession compiler)
{
    public ImmutableArray<XamlResourceReference> GetReferences(XamlAnalysis analysis)
    {
        var paths = compiler.ProjectDocuments.GroupBy(d => d.LogicalPath, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Syntax.Path, StringComparer.Ordinal);
        return BoundDocumentTraversal.Expressions(analysis.Document).OfType<BoundResourceExpression>().Select(reference =>
        {
            var resource = reference.Resource;
            var path = resource.LocalDocumentId != null && paths.TryGetValue(resource.LocalDocumentId, out var local) ? local : null;
            return new XamlResourceReference(reference.Span, resource.Uri, path, resource.RootType.ToDisplayString(), resource.ExternalFactory != null);
        }).Distinct().OrderBy(r => r.Span.Start).ToImmutableArray();
    }
    public ImmutableArray<XamlCompletionItem> GetCompletions(XamlAnalysis analysis, int position)
    {
        var element = analysis.Syntax.FindElement(position);
        if (element == null || analysis.Document.Options.Resources is not XamlResourceCatalog catalog) return ImmutableArray<XamlCompletionItem>.Empty;
        var scope = NamespaceScope.Empty;
        foreach (var ancestor in analysis.Syntax.Root!.DescendantsAndSelf().Where(e => e.Span.Contains(element.Span)).OrderByDescending(e => e.Span.Length)) scope = scope.Push(ancestor);
        var name = scope.Expand(element.Name);
        var type = name.Namespace == null ? null : compiler.Types.Resolve(name.Namespace, name.LocalName).Type;
        if (type == null || !analysis.Document.Profile.ResourceSourceMembers.TryGetValue(type.MetadataName(), out var member)) return ImmutableArray<XamlCompletionItem>.Empty;
        var attribute = element.Attributes.FirstOrDefault(a => a.Name == member && a.ValueSpan.Start <= position && position <= a.ValueSpan.End);
        if (attribute == null) return ImmutableArray<XamlCompletionItem>.Empty;
        var uri = analysis.Document.Options.ResourceUri ?? analysis.Document.Options.BaseUri;
        Uri? baseUri = uri != null && Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute : null;
        return catalog.Resources.GroupBy(r => r.Uri, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())
            .Where(r => r.Uri != uri).Select(resource =>
            {
                var target = new Uri(resource.Uri, UriKind.Absolute);
                var text = baseUri != null && baseUri.Scheme == target.Scheme && baseUri.Authority == target.Authority
                    ? baseUri.MakeRelativeUri(target).ToString() : resource.Uri;
                return new XamlCompletionItem(text, text, "File", resource.RootType.ToDisplayString() + (resource.ExternalFactory == null ? " · project resource" : " · referenced factory"));
            }).OrderBy(c => c.Label, StringComparer.Ordinal).ToImmutableArray();
    }
}
