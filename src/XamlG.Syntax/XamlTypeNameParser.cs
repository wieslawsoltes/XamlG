using System.Collections.Immutable;
namespace XamlG.Syntax;
public static class XamlTypeNameParser
{
    public static XamlTypeNameSyntax? Parse(string text, TextSpan span, Action<XamlDiagnostic> report)
    {
        if (Simple(text)) return new(text, ImmutableArray<XamlTypeNameSyntax>.Empty, false, new(span.Start, text.Length));
        var values = ParseList(text, span, report);
        if (values.Length == 1) return values[0];
        if (values.Length > 1) report(new("XG0011", "Expected one type name.", span));
        return null;
    }
    public static ImmutableArray<XamlTypeNameSyntax> ParseList(string text, TextSpan span, Action<XamlDiagnostic> report)
    {
        if (Simple(text)) return ImmutableArray.Create(new XamlTypeNameSyntax(text, ImmutableArray<XamlTypeNameSyntax>.Empty, false, new(span.Start, text.Length)));
        var position = 0; var failed = false; string? failure = null;
        void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        XamlTypeNameSyntax? Read(int depth)
        {
            Space(); var start = position;
            if (depth > 64) { failed = true; return null; }
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not '(' and not ')' and not ',' and not '?') position++;
            var name = text.Substring(start, position - start);
            if (name.Length == 0) { failed = true; return null; }
            Space(); var arguments = ImmutableArray<XamlTypeNameSyntax>.Empty;
            if (position < text.Length && text[position] == '(')
            {
                var items = ImmutableArray.CreateBuilder<XamlTypeNameSyntax>();
                position++;
                while (position < text.Length)
                {
                    var argument = Read(depth + 1); if (argument == null) return null; items.Add(argument); Space();
                    if (position < text.Length && text[position] == ',') { position++; continue; }
                    break;
                }
                if (position >= text.Length || text[position] != ')' || items.Count == 0) { failed = true; failure = "Unable to parse x:Type: Unmatched '(' in the generic argument list."; return null; }
                position++; Space();
                arguments = items.ToImmutable();
            }
            var nullable = position < text.Length && text[position] == '?'; if (nullable) position++;
            if (nullable && position < text.Length && text[position] == '?') { failed = true; failure = "A type name cannot have multiple nullable indicators."; return null; }
            return new(name, arguments, nullable, new(span.Start + start, position - start));
        }
        var result = ImmutableArray.CreateBuilder<XamlTypeNameSyntax>(); Space();
        while (position < text.Length)
        {
            var type = Read(0); if (type == null) break; result.Add(type); Space();
            if (position == text.Length) break;
            if (text[position++] != ',') { failed = true; break; }
            Space(); if (position == text.Length) failed = true;
        }
        if (failed || result.Count == 0) { report(new("XG0011", failure ?? "Invalid XAML type name or generic argument list.", span)); return ImmutableArray<XamlTypeNameSyntax>.Empty; }
        return result.ToImmutable();
    }

    // The common non-generic name needs neither a list nor an argument builder.
    // Leave whitespace, nullable suffixes, lists and errors to the full grammar.
    private static bool Simple(string text)
    {
        if (text.Length == 0) return false;
        foreach (var character in text)
            if (char.IsWhiteSpace(character) || character is '(' or ')' or ',' or '?') return false;
        return true;
    }
}
