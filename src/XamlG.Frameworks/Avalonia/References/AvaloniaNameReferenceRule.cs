using System.Threading;
using XamlG.Compiler.References;
using XamlG.Frameworks.Avalonia.Bindings;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Recognizes name references only on actual Avalonia binding types, not same-named custom extensions.</summary>
public sealed class AvaloniaNameReferenceRule : IXamlNameReferenceRule
{
    private static readonly HashSet<string> BindingTypes = new(StringComparer.Ordinal)
    {
        AvaloniaBindingMetadata.ReflectionBinding, AvaloniaBindingMetadata.ReflectionExtension,
        AvaloniaBindingMetadata.BindingExtension, AvaloniaBindingMetadata.CompiledBinding, AvaloniaBindingMetadata.CompiledExtension
    };

    public IEnumerable<XamlNameReference> GetReferences(XamlSyntaxTree tree, XamlElementSyntax element,
        NamespaceScope scope, RoslynTypeSystem types, CancellationToken cancellationToken)
    {
        bool IsBinding(string name)
        {
            var expanded = scope.Expand(name);
            if (expanded.Namespace == null) return false;
            var type = types.Resolve(expanded.Namespace, expanded.LocalName).Type ??
                types.Resolve(expanded.Namespace, expanded.LocalName + types.Configuration.MarkupExtensionSuffix).Type;
            return type != null && BindingTypes.Contains(type.MetadataName());
        }
        foreach (var attribute in element.Attributes.Where(a => XamlReferenceSyntax.IsRuntimeAttribute(a, scope, types)))
        {
            if (IsBinding(element.Name))
            {
                if (attribute.Name == AvaloniaBindingMetadata.ElementName) yield return new(attribute.Value, attribute.ValueSpan);
                if (attribute.Name == AvaloniaBindingMetadata.PathMember)
                    foreach (var item in PathReferences(XamlDecodedTextMap.Create(tree.Text, attribute.ValueSpan), attribute.Value, 0, cancellationToken)) yield return item;
            }
            foreach (var occurrence in XamlMarkupScanner.Scan(tree, attribute, cancellationToken))
            {
                if (!IsBinding(occurrence.Syntax.Name)) continue;
                foreach (var argument in occurrence.Syntax.Arguments)
                {
                    if (argument.ValueSpan is not { } span || occurrence.SourceMap.Text.Substring(span.Start, span.Length) != argument.Value) continue;
                    if (argument.Name == AvaloniaBindingMetadata.ElementName) yield return new(argument.Value, occurrence.SourceMap.ToSource(span));
                    else if (argument.Name is null or AvaloniaBindingMetadata.PathMember)
                        foreach (var reference in PathReferences(occurrence.SourceMap, argument.Value, span.Start, cancellationToken)) yield return reference;
                }
            }
        }
        // Property-element binding forms use the same name/path semantics as attributes.
        var dot = element.Name.LastIndexOf('.');
        if (dot > 0 && IsBinding(element.Name.Substring(0, dot)))
        {
            var member = element.Name.Substring(dot + 1);
            var nodes = element.Children.OfType<XamlTextSyntax>().ToArray();
            if (nodes.Length == 1 && !nodes[0].IsCData && !element.Children.OfType<XamlElementSyntax>().Any())
            {
                var map = XamlDecodedTextMap.Create(tree.Text, nodes[0].Span, attribute: false);
                var value = map.Text.Trim();
                var start = map.Text.Length - map.Text.TrimStart().Length;
                if (member == AvaloniaBindingMetadata.ElementName) yield return new(value, map.ToSource(new(start, value.Length)));
                else if (member == AvaloniaBindingMetadata.PathMember)
                    foreach (var reference in PathReferences(map, value, start, cancellationToken)) yield return reference;
            }
        }
    }
    private static IEnumerable<XamlNameReference> PathReferences(XamlDecodedTextMap map, string path, int start, CancellationToken token)
    {
        var errors = new List<XamlDiagnostic>();
        var parsed = BindingPathParser.Parse(path, new(start, path.Length), errors.Add, token);
        if (parsed == null || errors.Count != 0) yield break;
        foreach (var segment in parsed.Segments.Where(s => s.Kind == BindingPathKind.ElementName))
            yield return new(segment.Name, map.ToSource(new(segment.Span.Start + 1, segment.Name.Length)));
    }
}
