using System.Collections.Immutable;
using System.Threading;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Parses binding paths without consulting reflection or evaluating application expressions.</summary>
public sealed class BindingPathParser
{
    private readonly string _text;
    private readonly int _offset;
    private readonly Action<XamlDiagnostic> _report;
    private readonly CancellationToken _cancellation;
    private readonly ImmutableArray<BindingPathSegment>.Builder _segments = ImmutableArray.CreateBuilder<BindingPathSegment>();
    private int _position;
    private bool _failed;

    private BindingPathParser(string text, int offset, Action<XamlDiagnostic> report, CancellationToken cancellation)
    { _text = text; _offset = offset; _report = report; _cancellation = cancellation; }

    public static BindingPathSyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report, CancellationToken cancellationToken = default)
    {
        var parser = new BindingPathParser(text, span.Start, report, cancellationToken);
        parser.Read(0, false);
        return parser._failed ? null : new(parser._segments.ToImmutable(), span);
    }

    /// <summary>Parse decoded binding text while mapping segments and diagnostics to
    /// complete raw XML ranges, including encoded characters and surrounding whitespace.</summary>
    public static BindingPathSyntax? ParseAtSource(string text, TextSpan span, string source,
        Action<XamlDiagnostic> report, CancellationToken cancellationToken = default)
    {
        XamlDecodedTextMap map;
        try { map = XamlDecodedTextMap.Create(source, span); }
        catch (ArgumentException) { return Parse(text, span, report, cancellationToken); }
        var start = 0;
        if (map.Text != text)
        {
            if (map.Text.Trim() != text) return Parse(text, span, report, cancellationToken);
            start = map.Text.Length - map.Text.TrimStart().Length;
        }
        TextSpan Map(TextSpan value) => map.ToSource(new(value.Start + start, value.Length));
        var parsed = Parse(text, new(0, text.Length), diagnostic => report(diagnostic with { Span = Map(diagnostic.Span) }), cancellationToken);
        return parsed == null ? null : new(parsed.Segments.Select(segment => segment with { Span = Map(segment.Span) }).ToImmutableArray(), span);
    }

    private void Read(int depth, bool nested)
    {
        if (depth > 64) { Error("Binding expression nesting exceeds 64 levels."); return; }
        White();
        while (_position < _text.Length && _text[_position] == '!')
        { var start = _position++; _segments.Add(new(BindingPathKind.Not, string.Empty, Span(start))); }
        var needsSegment = false;
        var acceptsNull = false;
        while (_position < _text.Length && !_failed)
        {
            _cancellation.ThrowIfCancellationRequested(); White();
            if (_position == _text.Length || nested && _text[_position] == ')') break;
            var start = _position;
            if (_text[_position] == '?')
            {
                if (needsSegment || _segments.Count == 0 || _position + 1 >= _text.Length || _text[_position + 1] != '.')
                { Error("A null-conditional accessor requires a preceding value and '?.'."); break; }
                _position += 2; needsSegment = true; acceptsNull = true; continue;
            }
            if (_text[_position] == '.')
            {
                _position++;
                if (needsSegment) { Error("An empty property segment is not valid."); break; }
                needsSegment = _segments.Count != 0;
                continue;
            }
            if (_text[_position] == '^')
            {
                _position++;
                if (needsSegment) { Error("A stream operator cannot follow a member separator."); break; }
                _segments.Add(new(BindingPathKind.Stream, string.Empty, Span(start))); continue;
            }
            if (_text[_position] == '[')
            {
                if (needsSegment) { Error("An indexer cannot follow a member separator."); break; }
                var arguments = BracketArguments();
                _segments.Add(new(BindingPathKind.Indexer, string.Empty, Span(start)) { Arguments = arguments });
            }
            else if (_text[_position] == '#')
            {
                _position++; var name = Identifier();
                if (name.Length == 0) { Error("A named-element source requires a name."); break; }
                _segments.Add(new(BindingPathKind.ElementName, name, Span(start)));
            }
            else if (_text[_position] == '$')
            {
                _position++; var name = Identifier();
                if (name == "self") _segments.Add(new(BindingPathKind.Self, name, Span(start)));
                else if (name == "parent")
                {
                    var arguments = _position < _text.Length && _text[_position] == '[' ? BracketArguments(';') : ImmutableArray<string>.Empty;
                    _segments.Add(new(BindingPathKind.Parent, name, Span(start)) { Arguments = arguments });
                }
                else { Error("Unknown binding source '$" + name + "'."); break; }
            }
            else if (_text[_position] == '(')
            {
                _position++;
                if (_position < _text.Length && _text[_position] == '(')
                {
                    _position++; var typeStart = _position;
                    while (_position < _text.Length && _text[_position] != ')') _position++;
                    var type = _text.AsSpan(typeStart, _position - typeStart).Trim().ToString();
                    if (!Take(')') || type.Length == 0) { Error("An explicit cast requires a type name."); break; }
                    Read(depth + 1, true);
                    if (!Take(')')) { Error("Unclosed cast expression."); break; }
                    _segments.Add(new(BindingPathKind.Cast, type, Span(start)));
                }
                else
                {
                    var bodyStart = _position;
                    while (_position < _text.Length && _text[_position] != ')') _position++;
                    var body = _text.AsSpan(bodyStart, _position - bodyStart).Trim().ToString();
                    if (!Take(')') || body.Length == 0) { Error("Unclosed attached-property or cast expression."); break; }
                    _segments.Add(new(body.Contains('.') ? BindingPathKind.AttachedProperty : BindingPathKind.Cast, body, Span(start)));
                }
            }
            else
            {
                var name = Identifier();
                if (name.Length == 0) { Error("Unexpected character in binding path."); break; }
                _segments.Add(new(BindingPathKind.Property, name, Span(start)));
            }
            if (acceptsNull && _segments.Count != 0)
            {
                _segments[_segments.Count - 1] = _segments[_segments.Count - 1] with { AcceptsNull = true };
                acceptsNull = false;
            }
            needsSegment = false;
            White();
            if (_position < _text.Length && _text[_position] is not '.' and not '[' and not '^' and not '?' && !(nested && _text[_position] == ')'))
            { Error("Binding path segments must be separated by '.'."); break; }
        }
        if (needsSegment) Error("A binding path cannot end in '.'.");
    }

    private ImmutableArray<string> BracketArguments(char separator = ',')
    {
        _position++;
        var result = ImmutableArray.CreateBuilder<string>(); var start = _position; char quote = '\0';
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (quote != '\0') { if (c == quote) quote = '\0'; else if (c == '\\' && _position + 1 < _text.Length) _position++; }
            else if (c is '\'' or '"') quote = c;
            else if (c == separator || c == ']')
            {
                var argument = _text.AsSpan(start, _position - start).Trim().ToString();
                if (argument.Length == 0) { Error("An indexer argument cannot be empty."); return result.ToImmutable(); }
                result.Add(argument); _position++;
                if (c == ']') return result.ToImmutable();
                start = _position; continue;
            }
            _position++;
        }
        Error("Unclosed binding indexer."); return result.ToImmutable();
    }
    private string Identifier()
    {
        var start = _position;
        while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or ':' or '-')) _position++;
        return _text.Substring(start, _position - start);
    }
    private void White() { while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++; }
    private bool Take(char c) { if (_position == _text.Length || _text[_position] != c) return false; _position++; return true; }
    private TextSpan Span(int start) => new(_offset + start, _position - start);
    private void Error(string message)
    {
        if (_failed) return; _failed = true;
        _report(new("XG3200", message, new(_offset + _position, _position == _text.Length ? 0 : 1)));
    }
}
