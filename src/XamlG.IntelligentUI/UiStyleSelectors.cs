using System.Collections.Immutable;
using System.Globalization;

namespace XamlG.IntelligentUI;

public enum UiSelectorRelation { Root, Descendant, Child, Template }
public sealed record UiSelectorPosition(int Step, int Offset, bool FromEnd);
public sealed record UiSelectorCompound(string? Type, bool IncludeDerived, string? Name,
    ImmutableArray<string> Classes, ImmutableArray<string> PseudoClasses,
    ImmutableArray<UiSelectorCompound> Negations, ImmutableArray<UiSelectorPosition> Positions);
public sealed record UiSelectorStep(UiSelectorRelation Relation, UiSelectorCompound Predicate);

/// <summary>Bounded, non-executable selector syntax. Each selector ends in a registered type,
/// so its setters have one statically validated property contract. Groups lower to separate rules.</summary>
public static class UiStyleSelectors
{
    public const int MaximumSteps = 16;
    public const int MaximumPredicates = 64;
    public const int MaximumGroups = 16;
    public static UiStyleSelector Parse(string source)
    {
        var reader = new Reader(source); var steps = ImmutableArray.CreateBuilder<UiSelectorStep>();
        var relation = UiSelectorRelation.Root; reader.White();
        while (true)
        {
            if (steps.Count == MaximumSteps) throw Invalid("Selector step budget exceeded.");
            steps.Add(new(relation, reader.Compound(0)));
            var separated = reader.White();
            if (reader.End) break;
            if (reader.Take('>')) relation = UiSelectorRelation.Child;
            else if (reader.Take("/template/")) relation = UiSelectorRelation.Template;
            else if (separated) relation = UiSelectorRelation.Descendant;
            else throw Invalid("Expected a child, descendant or template combinator.");
            reader.White(); if (reader.End) throw Invalid("A selector cannot end with a combinator.");
        }
        var final = steps[^1].Predicate;
        if (final.Type is null) throw Invalid("The final selector requires a registered type for setter validation.");
        return new(final.Type, final.Name, final.Classes, final.PseudoClasses) { Steps = steps.ToImmutable() };
    }
    public static ImmutableArray<string> SplitGroups(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 512) throw Invalid("Selector exceeds its text budget.");
        var result = ImmutableArray.CreateBuilder<string>(); var start = 0; var depth = 0;
        for (var i = 0; i <= source.Length; i++)
        {
            if (i < source.Length)
            {
                if (source[i] == '(' && ++depth > 5) throw Invalid("Selector nesting budget exceeded.");
                if (source[i] == ')' && --depth < 0) throw Invalid("Unbalanced selector parentheses.");
            }
            if (i == source.Length || source[i] == ',' && depth == 0)
            {
                var item = source[start..i].Trim();
                if (item.Length == 0 || result.Count == MaximumGroups) throw Invalid("Invalid selector group.");
                result.Add(item); start = i + 1;
            }
        }
        if (depth != 0) throw Invalid("Unbalanced selector parentheses.");
        return result.ToImmutable();
    }
    public static void ValidateTypes(UiStyleSelector selector, UiCatalog catalog)
    {
        void Check(UiSelectorCompound compound)
        {
            if (compound.Type is { } type && !catalog.Components.ContainsKey(type)) throw Invalid("Unregistered selector type: " + type);
            foreach (var negative in compound.Negations) Check(negative);
        }
        foreach (var step in selector.Steps) Check(step.Predicate);
    }
    public static bool IsThemeSelector(UiStyleSelector selector, string owner) =>
        !selector.Steps.IsDefaultOrEmpty && selector.Steps[0].Predicate is { Type: var type, IncludeDerived: false } && type == owner;

    private sealed class Reader
    {
        private readonly string _source; private int _position, _predicates;
        internal Reader(string source)
        {
            if (string.IsNullOrEmpty(source) || source.Length > 512) throw Invalid("Selector exceeds its text budget.");
            _source = source;
        }
        internal bool End => _position == _source.Length;
        private char Peek => End ? '\0' : _source[_position];
        internal bool White() { var start = _position; while (!End && char.IsWhiteSpace(Peek)) _position++; return start != _position; }
        internal bool Take(char value) { if (Peek != value) return false; _position++; return true; }
        internal bool Take(string value)
        {
            if (!_source.AsSpan(_position).StartsWith(value, StringComparison.Ordinal)) return false;
            _position += value.Length; return true;
        }
        private string Identifier()
        {
            var start = _position;
            while (!End && (char.IsAsciiLetterOrDigit(Peek) || Peek is '_' or '-')) _position++;
            var result = _source[start.._position];
            if (!UiStyles.IsIdentifier(result)) throw Invalid("Expected a selector identifier.");
            return result;
        }
        private void Require(char value) { if (!Take(value)) throw Invalid("Incomplete selector function."); }
        internal UiSelectorCompound Compound(int depth)
        {
            if (depth > 4) throw Invalid("Selector negation nesting budget exceeded.");
            var start = _position; string? type = null, name = null; var derived = false;
            if (char.IsAsciiLetter(Peek) || Peek == '_') type = Identifier(); else Take('*');
            var classes = ImmutableArray.CreateBuilder<string>(); var pseudos = ImmutableArray.CreateBuilder<string>();
            var negatives = ImmutableArray.CreateBuilder<UiSelectorCompound>(); var positions = ImmutableArray.CreateBuilder<UiSelectorPosition>();
            while (Peek is '.' or '#' or ':')
            {
                if (++_predicates > MaximumPredicates) throw Invalid("Selector predicate budget exceeded.");
                var token = _source[_position++]; var value = Identifier();
                if (token == '.') { if (classes.Contains(value)) throw Invalid("Duplicate selector class."); classes.Add(value); }
                else if (token == '#') { if (name != null) throw Invalid("Duplicate selector name."); name = value; }
                else if (value == "is")
                {
                    if (type != null) throw Invalid("Duplicate selector type.");
                    Require('('); White(); type = Identifier(); White(); Require(')'); derived = true;
                }
                else if (value == "not") { Require('('); White(); negatives.Add(Compound(depth + 1)); White(); Require(')'); }
                else if (value is "nth-child" or "nth-last-child")
                {
                    Require('('); var begin = _position;
                    while (!End && Peek != ')') _position++;
                    var formula = _source[begin.._position]; Require(')');
                    var fromEnd = value == "nth-last-child";
                    if (positions.Any(position => position.FromEnd == fromEnd)) throw Invalid("Duplicate positional selector.");
                    positions.Add(Position(formula, fromEnd));
                }
                else
                {
                    if (!UiStyles.IsPseudoClass(value) || pseudos.Contains(value)) throw Invalid("Unsupported or duplicate pseudoclass.");
                    pseudos.Add(value);
                }
            }
            if (_position == start) throw Invalid("Expected a selector predicate.");
            return new(type, derived, name, classes.ToImmutable(), pseudos.ToImmutable(), negatives.ToImmutable(), positions.ToImmutable());
        }
    }
    private static UiSelectorPosition Position(string source, bool fromEnd)
    {
        if (source.Length > 64) throw Invalid("Positional formula exceeds its budget.");
        var value = string.Concat(source.Where(c => !char.IsWhiteSpace(c)));
        if (value == "odd") return new(2, 1, fromEnd);
        if (value == "even") return new(2, 0, fromEnd);
        int Number(string text) => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n is >= -4096 and <= 4096
            ? n : throw Invalid("Positional coefficients must be integers in [-4096,4096].");
        var index = value.IndexOf('n');
        if (index < 0) return new(0, Number(value), fromEnd);
        var prefix = value[..index]; var suffix = value[(index + 1)..];
        var step = prefix switch { "" or "+" => 1, "-" => -1, _ => Number(prefix) };
        if (suffix.Length > 0 && suffix[0] is not ('+' or '-')) throw Invalid("A positional offset requires a sign.");
        return new(step, suffix.Length == 0 ? 0 : Number(suffix), fromEnd);
    }
    private static UiException Invalid(string text) => new("invalid_style", text);
}
