using System.Collections.Immutable;
using System.Text;
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
        _context.Cancellation.ThrowIfCancellationRequested();
        var merged = MergeText(source);
        var significant = collection && _context.Types.HasInheritedAttribute(target, _context.Types.Configuration.WhitespaceSignificantCollectionAttributes);
        bool Trims(XamlSyntaxNode? node) => node is XamlElementSyntax element &&
            _context.Values.PeekNodeType(element, scope) is { } type && _context.Types.HasInheritedAttribute(type, _context.Types.Configuration.TrimSurroundingWhitespaceAttributes);
        var written = 0;
        XamlSyntaxNode? previous = null;
        for (var i = 0; i < merged.Count; i++)
        {
            _context.Cancellation.ThrowIfCancellationRequested();
            var node = merged[i];
            // Compaction may overwrite the preceding slot. Trimming must inspect
            // original neighbors, including nodes whose normalized text is dropped.
            var preceding = previous;
            previous = node;
            XamlElementSyntax? literal = null;
            var preserve = scope.PreserveSpace;
            var text = node as XamlTextSyntax;
            if (text == null && node is XamlElementSyntax element && IsTextElement(element, scope, out var nested) &&
                _context.Values.TryGetStringLiteral(element, scope, out var content) && content.Length > 0)
            {
                literal = element;
                preserve = (nested ?? scope.Push(element)).PreserveSpace;
                text = new XamlTextSyntax(content, false, element.Span);
            }
            if (text == null) { merged[written++] = node; continue; }
            if (directCollection && IsXmlWhitespace(text.Value)) continue;
            var value = preserve ? text.Value : XmlWhitespace.Collapse(text.Value,
                !significant || i == 0 || Trims(preceding),
                !significant || i == merged.Count - 1 || Trims(merged[i + 1]));
            if (value.Length == 0 || collection && !significant && string.IsNullOrWhiteSpace(value)) continue;
            // Retain literal object form, so downstream conversion still uses the
            // runtime-string path. Unchanged immutable text needs no record copy.
            merged[written++] = literal == null ? value == text.Value ? text : text with { Value = value } :
                value == text.Value ? literal : literal with { Children = ImmutableArray.Create<XamlSyntaxNode>(text with { Value = value }) };
        }
        if (written != merged.Count) merged.RemoveRange(written, merged.Count - written);
        return merged.ToArray();
    }

    private List<XamlSyntaxNode> MergeText(IEnumerable<XamlSyntaxNode> source)
    {
        var merged = new List<XamlSyntaxNode>();
        XamlTextSyntax? first = null;
        StringBuilder? text = null;
        TextSpan span = default;
        void Flush()
        {
            if (first == null) return;
            merged.Add(text == null ? first : first with { Value = text.ToString(), FullSpan = span });
            first = null;
            text = null;
        }
        // Enumerate once. Comments and other trivia do not terminate a text run.
        // One builder per fragmented run avoids repeatedly copying growing prefixes.
        foreach (var node in source)
        {
            _context.Cancellation.ThrowIfCancellationRequested();
            if (node is XamlTextSyntax fragment)
            {
                if (first == null) { first = fragment; span = fragment.Span; }
                else
                {
                    span = TextSpan.FromBounds(first.Span.Start, fragment.Span.End);
                    (text ??= new StringBuilder(first.Value)).Append(fragment.Value);
                }
            }
            else if (node is XamlElementSyntax) { Flush(); merged.Add(node); }
        }
        Flush();
        return merged;
    }

    private static bool IsTextElement(XamlElementSyntax element, NamespaceScope scope, out NamespaceScope? nested)
    {
        nested = null;
        foreach (var child in element.Children) if (child is XamlElementSyntax) return false;
        foreach (var attribute in element.Attributes)
        {
            if (attribute.IsNamespace) continue;
            // NamespaceScope is sealed and immutable. Build it once, not once per
            // attribute; type-binding callbacks remain in their original call sites.
            nested ??= scope.Push(element);
            if (nested.Expand(attribute.Name, true).Namespace != XamlNames.Xml) return false;
        }
        return true;
    }

    private static bool IsXmlWhitespace(string text)
    {
        foreach (var character in text) if (!XmlWhitespace.IsWhitespace(character)) return false;
        return true;
    }
}
