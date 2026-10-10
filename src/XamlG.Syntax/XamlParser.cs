using System.Collections.Immutable;
using System.Threading;
namespace XamlG.Syntax;

/// <summary>Error-recovering XML frontend. It never resolves entities, opens URIs, or loads types.</summary>
internal sealed class XamlParser
{
    private readonly string _text;
    private readonly XamlParseOptions _options;
    private readonly CancellationToken _cancellation;
    private readonly List<XamlDiagnostic> _diagnostics = new();
    private XmlNamePool? _namePool;
    private int _position;
    public XamlParser(string text, XamlParseOptions options, CancellationToken cancellation)
    { _text = text; _options = options; _cancellation = cancellation; }
    public (ImmutableArray<XamlSyntaxNode> Nodes, ImmutableArray<XamlDiagnostic> Diagnostics) Parse()
    {
        if (_text.Length > _options.MaximumCharacters)
        { Report("XG0001", "Document exceeds the configured source size limit.", 0, 0); return (ImmutableArray<XamlSyntaxNode>.Empty, _diagnostics.ToImmutableArray()); }
        var nodes = ImmutableArray.CreateBuilder<XamlSyntaxNode>();
        while (_position < _text.Length)
        {
            _cancellation.ThrowIfCancellationRequested();
            var start = _position;
            if (At("</"))
            { SkipThrough(">"); Report("XG0004", "Closing tag has no matching opening tag.", start, _position - start); nodes.Add(new XamlTriviaSyntax("InvalidCloseTag", new(start, _position - start))); }
            else nodes.Add(ParseNode(0));
            if (_position == start) _position++;
        }
        var roots = nodes.OfType<XamlElementSyntax>().ToArray();
        if (roots.Length == 0) Report("XG0002", "A XAML document requires a root element.", 0, 0);
        foreach (var extra in roots.Skip(1)) Report("XG0002", "A XAML document cannot contain multiple root elements.", extra.NameSpan.Start, extra.NameSpan.Length);
        foreach (var text in nodes.OfType<XamlTextSyntax>())
            if (!string.IsNullOrWhiteSpace(text.Value) && text.Value != "\uFEFF") Report("XG0002", "Text is not allowed outside the root element.", text.Span.Start, text.Span.Length);
        return (nodes.ToImmutable(), _diagnostics.ToImmutableArray());
    }
    private XamlSyntaxNode ParseNode(int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        var start = _position;
        if (At("<!--"))
        { _position += 4; var body = _position; var ended = SkipThrough("-->"); if (!ended) Report("XG0003", "Unterminated XML comment.", start, _position - start); return new XamlTriviaSyntax("Comment", new(start, _position - start)); }
        if (At("<![CDATA["))
        {
            _position += 9; var content = _position; var end = _text.IndexOf("]]>", _position, StringComparison.Ordinal);
            if (end < 0) { end = _text.Length; _position = end; Report("XG0003", "Unterminated CDATA section.", start, end - start); } else _position = end + 3;
            return new XamlTextSyntax(_text.Substring(content, end - content).Replace("\r\n", "\n").Replace('\r', '\n'), true, new(start, _position - start));
        }
        if (At("<?"))
        { _position += 2; if (!SkipThrough("?>")) Report("XG0003", "Unterminated processing instruction.", start, _position - start); return new XamlTriviaSyntax("ProcessingInstruction", new(start, _position - start)); }
        if (At("<!"))
        {
            var bracket = 0; char quote = '\0'; _position += 2;
            while (_position < _text.Length)
            {
                var c = _text[_position++];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') quote = c; else if (c == '[') bracket++; else if (c == ']') bracket--; else if (c == '>' && bracket <= 0) break;
            }
            Report("XG0009", "DTD and entity declarations are not permitted in XAML.", start, _position - start);
            return new XamlTriviaSyntax("ForbiddenDeclaration", new(start, _position - start));
        }
        if (_text[_position] == '<') return ParseElement(depth);
        _position = XamlTextScanner.TextEnd(_text, _position);
        return new XamlTextSyntax(XmlEntityDecoder.Decode(_text.AsSpan(start, _position - start), start, AddDiagnostic, false), false, new(start, _position - start));
    }
    private XamlElementSyntax ParseElement(int depth)
    {
        var start = _position++; var nameStart = _position; var name = ReadName();
        var nameSpan = new TextSpan(nameStart, _position - nameStart);
        if (name.Length == 0) Report("XG0004", "Expected an element name.", nameStart, 0);
        ImmutableArray<XamlAttributeSyntax>.Builder? attributes = null;
        HashSet<string>? names = null; var selfClosing = false;
        while (_position < _text.Length)
        {
            _cancellation.ThrowIfCancellationRequested(); SkipWhitespace();
            if (At("/>")) { _position += 2; selfClosing = true; break; }
            if (At(">")) { _position++; break; }
            if (At("<")) { Report("XG0004", "Expected '>' before the next element.", _position, 0); break; }
            var attributeStart = _position; var attributeName = ReadName(); var attributeNameSpan = new TextSpan(attributeStart, _position - attributeStart);
            if (attributeName.Length == 0) { if (_position < _text.Length) { Report("XG0005", "Invalid attribute name.", _position, 1); _position++; } continue; }
            attributes ??= ImmutableArray.CreateBuilder<XamlAttributeSyntax>(4);
            if (DuplicateAttribute(attributes, ref names, attributeName)) Report("XG0006", $"Duplicate attribute '{attributeName}'.", attributeStart, attributeName.Length);
            SkipWhitespace();
            if (!At("=")) { Report("XG0005", "Expected '=' after the attribute name.", _position, 0); attributes.Add(new(attributeName, string.Empty, attributeNameSpan, new(_position, 0), new(attributeStart, _position - attributeStart), '"')); continue; }
            _position++; SkipWhitespace();
            var quote = _position < _text.Length && (_text[_position] is '\'' or '"') ? _text[_position++] : '\0';
            var valueStart = _position;
            if (quote == '\0')
            {
                Report("XG0005", "Attribute values must be quoted.", _position, 0);
                while (_position < _text.Length && !XmlWhitespace.IsWhitespace(_text[_position]) && _text[_position] != '>' && !At("/>") && _text[_position] != '<') _position++;
            }
            else
            {
                _position = XamlTextScanner.AttributeValueEnd(_text, _position, quote);
                if (_position == _text.Length || _text[_position] != quote) Report("XG0005", "Unterminated attribute value.", valueStart, _position - valueStart);
            }
            var valueSpan = new TextSpan(valueStart, _position - valueStart);
            var value = XmlEntityDecoder.Decode(_text.AsSpan(valueStart, valueSpan.Length), valueStart, AddDiagnostic, true);
            if (quote != '\0' && _position < _text.Length && _text[_position] == quote) _position++;
            attributes.Add(new(attributeName, value, attributeNameSpan, valueSpan, new(attributeStart, _position - attributeStart), quote == '\0' ? '"' : quote));
        }
        var opening = new TextSpan(start, _position - start); var closing = new TextSpan(_position, 0); var closingName = closing;
        ImmutableArray<XamlSyntaxNode>.Builder? children = null;
        if (!selfClosing)
        {
            if (depth >= _options.MaximumDepth)
            { Report("XG0001", "Document exceeds the configured nesting depth limit.", start, opening.Length); _position = _text.Length; }
            else
            {
                var closed = false;
                while (_position < _text.Length)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (At("</"))
                    {
                        var closeStart = _position; var saved = _position; _position += 2; var closeNameStart = _position; var closeName = ReadName();
                        if (closeName != name)
                        {
                            _position = saved; Report("XG0007", $"Element '{name}' is not closed before '</{closeName}>'.", closeStart, 2 + closeName.Length); break;
                        }
                        closingName = new(closeNameStart, closeName.Length); SkipWhitespace();
                        if (At(">")) _position++; else Report("XG0004", "Expected '>' after the closing tag.", _position, 0);
                        closing = new(closeStart, _position - closeStart); closed = true; break;
                    }
                    var before = _position;
                    (children ??= ImmutableArray.CreateBuilder<XamlSyntaxNode>(4)).Add(ParseNode(depth + 1));
                    if (_position == before) _position++;
                }
                if (!closed && _position >= _text.Length) Report("XG0007", $"Element '{name}' is missing a closing tag.", nameStart, name.Length);
            }
        }
        return new(name, nameSpan, opening, closing, attributes?.ToImmutable() ?? ImmutableArray<XamlAttributeSyntax>.Empty,
            children?.ToImmutable() ?? ImmutableArray<XamlSyntaxNode>.Empty, selfClosing, new(start, _position - start)) { EndNameSpan = closingName };
    }
    private static bool DuplicateAttribute(ImmutableArray<XamlAttributeSyntax>.Builder attributes, ref HashSet<string>? names, string name)
    {
        // Most XAML elements have very few attributes. A bounded linear scan avoids
        // a hash table in that case; larger elements retain expected O(a) total work.
        if (names != null) return !names.Add(name);
        if (attributes.Count < 8)
        {
            foreach (var attribute in attributes) if (attribute.Name == name) return true;
            return false;
        }
        names = new(StringComparer.Ordinal);
        foreach (var attribute in attributes) names.Add(attribute.Name);
        return !names.Add(name);
    }
    private string ReadName()
    {
        var start = _position;
        _position = XamlTextScanner.XmlNameEnd(_text, _position, out var hash);
        return (_namePool ??= new(_text)).Get(start, _position - start, hash);
    }
    private void SkipWhitespace() => _position = XamlTextScanner.SkipXmlWhitespace(_text, _position);
    private bool At(string value) => _position <= _text.Length - value.Length && string.CompareOrdinal(_text, _position, value, 0, value.Length) == 0;
    private bool SkipThrough(string terminal) { var end = _text.IndexOf(terminal, _position, StringComparison.Ordinal); _position = end < 0 ? _text.Length : end + terminal.Length; return end >= 0; }
    private void Report(string code, string message, int start, int length) => AddDiagnostic(new(code, message, new(start, length)));
    private void AddDiagnostic(XamlDiagnostic diagnostic) { if (_diagnostics.Count < _options.MaximumDiagnostics) _diagnostics.Add(diagnostic); }
}
