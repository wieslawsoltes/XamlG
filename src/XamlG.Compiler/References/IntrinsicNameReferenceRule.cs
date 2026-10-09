using System.Threading;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler.References;

public sealed class IntrinsicNameReferenceRule : IXamlNameReferenceRule
{
    public IEnumerable<XamlNameReference> GetReferences(XamlSyntaxTree tree, XamlElementSyntax element,
        NamespaceScope scope, RoslynTypeSystem types, CancellationToken cancellationToken)
    {
        bool Reference(string name)
        {
            var expanded = scope.Expand(name);
            return expanded.Namespace != null && XamlNames.IsLanguage(expanded.Namespace) && expanded.LocalName == "Reference";
        }
        if (Reference(element.Name))
            foreach (var attribute in element.Attributes.Where(a => a.Name == "Name")) yield return new(attribute.Value, attribute.ValueSpan);
        foreach (var attribute in element.Attributes.Where(a => XamlReferenceSyntax.IsRuntimeAttribute(a, scope, types)))
            foreach (var occurrence in XamlMarkupScanner.Scan(tree, attribute, cancellationToken))
            {
                if (!Reference(occurrence.Syntax.Name)) continue;
                var argument = occurrence.Syntax.Arguments.FirstOrDefault(a => a.Name is null or "Name");
                if (argument?.ValueSpan is not { } span || !occurrence.SourceMap.Text.AsSpan(span.Start, span.Length).SequenceEqual(argument.Value.AsSpan())) continue;
                yield return new(argument.Value, occurrence.SourceMap.ToSource(span));
            }
    }
}
