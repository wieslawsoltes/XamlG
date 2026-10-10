using System.Buffers;
using System.Collections.Immutable;
using XamlG.Internal;
using System.Globalization;
using System.Threading;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Source-located selector grammar. Parsing never loads Avalonia or executes selector code.</summary>
public sealed class AvaloniaSelectorParser
{
    private readonly string _text;
    private readonly int _sourceOffset;
    private readonly Action<XamlDiagnostic> _report;
    private readonly CancellationToken _cancellation;
    private int _position;
    private bool _failed;

    private AvaloniaSelectorParser(string text, int offset, Action<XamlDiagnostic> report, CancellationToken cancellation)
    { _text = text; _sourceOffset = offset; _report = report; _cancellation = cancellation; }

    public static SelectorListSyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report,
        CancellationToken cancellationToken = default)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var parser = new AvaloniaSelectorParser(text, span.Start, report, cancellationToken);
        var result = parser.List(0, false);
        return parser._failed ? null : result;
    }

    private SelectorListSyntax List(int depth, bool nested)
    {
        var start = _position;
        var sequences = ImmutableArray.CreateBuilder<SelectorSequenceSyntax>();
        if (depth > 64) { Error("Selector nesting exceeds 64 levels.", start); return new(sequences.ToImmutable(), Span(start)); }
        while (!_failed)
        {
            White();
            var sequenceStart = _position;
            var steps = ImmutableArray.CreateBuilder<SelectorStepSyntax>();
            var compound = false;
            while (_position < _text.Length && !_failed)
            {
                _cancellation.ThrowIfCancellationRequested();
                var whitespaceStart = _position;
                var whitespace = White();
                if (_position == _text.Length || _text[_position] == ',' || nested && _text[_position] == ')') break;
                var tokenStart = _position;
                if (_text[_position] == '>' || At("/template/"))
                {
                    if (steps.Count == 0 || steps[steps.Count - 1].Kind == SelectorStepKind.Template)
                    { Error("A combinator requires a preceding selector.", tokenStart); break; }
                    var kind = _text[_position] == '>' ? SelectorStepKind.Child : SelectorStepKind.Template;
                    _position += kind == SelectorStepKind.Child ? 1 : 10;
                    steps.Add(new(kind, string.Empty, null, Span(tokenStart)));
                    compound = false;
                    continue;
                }
                if (whitespace && compound)
                {
                    steps.Add(new(SelectorStepKind.Descendant, string.Empty, null, new(_sourceOffset + whitespaceStart, tokenStart - whitespaceStart)));
                    compound = false;
                }
                SelectorStepSyntax? step = null;
                switch (_text[_position])
                {
                    case '^':
                        _position++;
                        step = new(SelectorStepKind.Nesting, string.Empty, null, Span(tokenStart));
                        break;
                    case '*':
                        _position++;
                        if (compound) { Error("A universal type selector must start a compound selector.", tokenStart); break; }
                        step = new(SelectorStepKind.Universal, string.Empty, null, Span(tokenStart));
                        break;
                    case '.': case '#':
                        var kind = _text[_position++] == '.' ? SelectorStepKind.Class : SelectorStepKind.Name;
                        var identifier = Identifier(false);
                        if (identifier.Length == 0) { Error("Expected a selector name.", tokenStart); break; }
                        step = new(kind, identifier, null, Span(tokenStart));
                        break;
                    case ':':
                        _position++;
                        var pseudo = Identifier(false);
                        if (pseudo.Length == 0) { Error("Expected a pseudo-class name.", tokenStart); break; }
                        if (_position < _text.Length && _text[_position] == '(')
                        {
                            _position++;
                            if (pseudo == "not")
                            {
                                var argument = List(depth + 1, true);
                                if (!Take(')')) { Error("Expected ')' after :not.", _position); break; }
                                step = new(SelectorStepKind.Not, pseudo, null, Span(tokenStart)) { Argument = argument };
                            }
                            else if (pseudo == "is")
                            {
                                White(); var type = Identifier(true); White();
                                if (type.Length == 0 || !Take(')')) { Error("Expected one type name in :is(...).", tokenStart); break; }
                                step = new(SelectorStepKind.Is, type, null, Span(tokenStart));
                            }
                            else if (pseudo is "nth-child" or "nth-last-child")
                            {
                                var argumentStart = _position;
                                while (_position < _text.Length && _text[_position] != ')') _position++;
                                var argument = _text.AsSpan(argumentStart, _position - argumentStart);
                                if (!Take(')') || !Nth(argument, out var coefficient, out var offset)) { Error("Invalid nth-child expression.", tokenStart); break; }
                                step = new(pseudo == "nth-child" ? SelectorStepKind.NthChild : SelectorStepKind.NthLastChild, pseudo, null, Span(tokenStart)) { Step = coefficient, Offset = offset };
                            }
                            else Error("Unknown selector function ':" + pseudo + "'.", tokenStart);
                        }
                        else step = new(SelectorStepKind.Class, ":" + pseudo, null, Span(tokenStart));
                        break;
                    case '[':
                        _position++; White(); var propertyStart = _position; var parenthesis = 0;
                        while (_position < _text.Length)
                        {
                            var c = _text[_position];
                            if (c == '(') parenthesis++;
                            if (c == ')') parenthesis--;
                            if (parenthesis == 0 && c == '=') break;
                            if (c == ']') break;
                            _position++;
                        }
                        var property = _text.AsSpan(propertyStart, _position - propertyStart).Trim().ToString();
                        if (property.Length == 0 || parenthesis != 0 || !Take('=')) { Error("Property selectors require [Property=Value].", tokenStart); break; }
                        var valueStart = _position;
                        while (_position < _text.Length && _text[_position] != ']') _position++;
                        var value = _text.Substring(valueStart, _position - valueStart);
                        if (!Take(']')) { Error("Expected ']' after a property selector.", tokenStart); break; }
                        step = new(SelectorStepKind.PropertyEquals, property, value, Span(tokenStart));
                        break;
                    default:
                        var name = Identifier(true);
                        if (name.Length == 0) { Error("Expected a selector type, class, name or pseudo-class.", tokenStart); break; }
                        step = new(SelectorStepKind.Type, name, null, Span(tokenStart));
                        break;
                }
                if (step != null) { steps.Add(step); compound = true; }
            }
            if (steps.Count == 0) Error("A selector sequence cannot be empty.", sequenceStart);
            sequences.Add(new(steps.ToImmutable(), Span(sequenceStart)));
            if (!Take(',')) break;
        }
        return new(sequences.ToImmutable(), Span(start));
    }

    private string Identifier(bool type)
    {
        var start = _position;
        while (_position < _text.Length)
        {
            var c = _text[_position];
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' || type && c == '|')) break;
            _position++;
        }
        return _text.Substring(start, _position - start);
    }
    private bool White()
    {
        var start = _position;
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        return start != _position;
    }
    private bool Take(char c) { if (_position == _text.Length || _text[_position] != c) return false; _position++; return true; }
    private bool At(string text) => _position <= _text.Length - text.Length && string.CompareOrdinal(_text, _position, text, 0, text.Length) == 0;
    private TextSpan Span(int start) => new(_sourceOffset + start, _position - start);
    private void Error(string message, int start)
    {
        if (_failed) return;
        _failed = true;
        _report(new("XG3100", message, new(_sourceOffset + start, Math.Min(1, _text.Length - start))));
    }
    private static bool Nth(ReadOnlySpan<char> text, out int step, out int offset)
    {
        // Most selectors contain no whitespace. Compact only when it is present,
        // retaining the pinned grammar's removal of whitespace even inside numbers.
        var whitespace = 0;
        while (whitespace < text.Length && !char.IsWhiteSpace(text[whitespace])) whitespace++;
        if (whitespace == text.Length) return NthCore(text, out step, out offset);
        char[]? rented = null;
        Span<char> compact = text.Length <= 256 ? stackalloc char[text.Length] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        try
        {
            text.Slice(0, whitespace).CopyTo(compact);
            var length = whitespace;
            for (var i = whitespace + 1; i < text.Length; i++)
                if (!char.IsWhiteSpace(text[i])) compact[length++] = text[i];
            return NthCore(compact.Slice(0, length), out step, out offset);
        }
        finally { if (rented != null) ArrayPool<char>.Shared.Return(rented); }
    }

    private static bool NthCore(ReadOnlySpan<char> text, out int step, out int offset)
    {
        step = 0; offset = 0;
        if (text.SequenceEqual("odd".AsSpan())) { step = 2; offset = 1; return true; }
        if (text.SequenceEqual("even".AsSpan())) { step = 2; return true; }
        var n = text.IndexOf('n');
        if (n < 0) return SpanNumberParser.TryParseInt(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset);
        var coefficient = text.Slice(0, n);
        if (coefficient.IsEmpty || coefficient.SequenceEqual("+".AsSpan())) step = 1;
        else if (coefficient.SequenceEqual("-".AsSpan())) step = -1;
        else if (!SpanNumberParser.TryParseInt(coefficient, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out step)) return false;
        return n == text.Length - 1 || SpanNumberParser.TryParseInt(text.Slice(n + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset);
    }
}
