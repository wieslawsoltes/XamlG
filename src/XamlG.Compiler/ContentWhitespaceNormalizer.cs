using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

/// <summary>Normalizes content once, after comment removal and before collection overload resolution.</summary>
internal sealed class ContentWhitespaceNormalizer
{
    private readonly BindingContext _context;
    public ContentWhitespaceNormalizer(BindingContext context) => _context = context;
    public XamlSyntaxNode[] Normalize(IEnumerable<XamlSyntaxNode> source, ITypeSymbol target, bool collection, NamespaceScope scope, bool directCollection = false)
    {
        var merged = new List<XamlSyntaxNode>();
        foreach (var node in source)
        {
            if (node is not XamlTextSyntax && node is not XamlElementSyntax) continue;
            if (node is XamlTextSyntax text && merged.LastOrDefault() is XamlTextSyntax previous)
                merged[merged.Count - 1] = previous with { Value = previous.Value + text.Value, FullSpan = TextSpan.FromBounds(previous.Span.Start, text.Span.End) };
            else merged.Add(node);
        }
        var significant = collection && _context.Types.HasInheritedAttribute(target, _context.Types.Configuration.WhitespaceSignificantCollectionAttributes);
        bool Trims(int index) => index >= 0 && index < merged.Count && merged[index] is XamlElementSyntax element &&
            _context.Values.PeekNodeType(element, scope) is { } type && _context.Types.HasInheritedAttribute(type, _context.Types.Configuration.TrimSurroundingWhitespaceAttributes);
        var result = new List<XamlSyntaxNode>(merged.Count);
        for (var i = 0; i < merged.Count; i++)
        {
            var node = merged[i];
            XamlElementSyntax? literal = null;
            var preserve = scope.PreserveSpace;
            var text = node as XamlTextSyntax;
            if (text == null && node is XamlElementSyntax element && !element.Children.OfType<XamlElementSyntax>().Any() &&
                element.Attributes.All(attribute => attribute.IsNamespace || scope.Push(element).Expand(attribute.Name, true).Namespace == XamlNames.Xml) &&
                _context.Values.TryGetStringLiteral(element, scope, out var content) && content.Length > 0)
            {
                literal = element;
                preserve = scope.Push(element).PreserveSpace;
                text = new XamlTextSyntax(content, false, element.Span);
            }
            if (text == null) { result.Add(node); continue; }
            if (directCollection && text.Value.All(XmlWhitespace.IsWhitespace)) continue;
            var value = preserve ? text.Value : XmlWhitespace.Collapse(text.Value,
                !significant || i == 0 || Trims(i - 1), !significant || i == merged.Count - 1 || Trims(i + 1));
            if (value.Length == 0 || collection && !significant && string.IsNullOrWhiteSpace(value)) continue;
            // Retain the object form so downstream conversion still takes the runtime-string path.
            result.Add(literal == null ? text with { Value = value } : value == text.Value ? literal :
                literal with { Children = ImmutableArray.Create<XamlSyntaxNode>(text with { Value = value }) });
        }
        return result.ToArray();
    }
}
