using System.Collections.Immutable;
using System.Threading;

namespace XamlG.Syntax;

/// <summary>Reparses the smallest complete element containing an edit batch. Unchanged subtrees retain
/// identity when their positions do not move. Recovery-sensitive edits conservatively use the full parser.</summary>
internal static class XamlIncrementalParser
{
    public static XamlSyntaxTree Parse(XamlSyntaxTree previous, string text, IReadOnlyList<XamlTextChange> changes,
        CancellationToken cancellationToken)
    {
        var version = checked(previous.Version + 1);
        XamlSyntaxTree Full() => XamlSyntaxTree.Parse(text, previous.Path, cancellationToken, previous.Options, version);
        if (previous.Diagnostics.Length != 0 || previous.Root == null || text.Length > previous.Options.MaximumCharacters) return Full();
        var edited = TextSpan.FromBounds(changes[0].Span.Start, changes[changes.Count - 1].Span.End);
        var target = previous.Root;
        var depth = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = FindContainingChild(target.Children, edited);
            if (candidate == null) break;
            target = candidate;
            depth++;
        }
        if (depth >= previous.Options.MaximumDepth || ReferenceEquals(target, previous.Root)) return Full();
        var delta = text.Length - previous.Text.Length;
        var newLength = checked(target.Span.Length + delta);
        if (newLength <= 0 || target.Span.Start + newLength > text.Length) return Full();
        var fragment = text.Substring(target.Span.Start, newLength);
        var options = previous.Options with { MaximumDepth = Math.Max(1, previous.Options.MaximumDepth - depth) };
        var parsed = XamlSyntaxTree.Parse(fragment, previous.Path, cancellationToken, options);
        if (parsed.Diagnostics.Length != 0 || parsed.Root == null || parsed.Root.Span.Start != 0 || parsed.Root.Span.Length != fragment.Length)
            return Full();

        var reused = 0;
        var replacement = (XamlElementSyntax)Shift(parsed.Root, target.Span.Start, cancellationToken);
        XamlSyntaxNode Rewrite(XamlSyntaxNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(node, target)) return replacement;
            if (node.Span.End <= target.Span.Start || node.Span.Start >= target.Span.End && delta == 0)
            {
                reused += Count(node);
                return node;
            }
            if (node.Span.Start >= target.Span.End) return Shift(node, delta, cancellationToken);
            if (node is not XamlElementSyntax element)
                throw new InvalidOperationException("The incremental edit boundary crossed a non-element node.");
            return element with
            {
                Children = element.Children.Select(Rewrite).ToImmutableArray(),
                CloseTagSpan = Offset(element.CloseTagSpan, delta),
                EndNameSpan = Offset(element.EndNameSpan, delta),
                FullSpan = new(element.Span.Start, checked(element.Span.Length + delta))
            };
        }
        var nodes = previous.Nodes.Select(Rewrite).ToImmutableArray();
        return new XamlSyntaxTree(text, previous.Path, version, previous.Options, nodes,
            ImmutableArray<XamlDiagnostic>.Empty, new(fragment.Length, reused, true));
    }

    private static XamlElementSyntax? FindContainingChild(ImmutableArray<XamlSyntaxNode> children, TextSpan span)
    {
        var low = 0;
        var high = children.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (children[middle].Span.Start <= span.Start) low = middle + 1;
            else high = middle - 1;
        }
        return high >= 0 && children[high] is XamlElementSyntax candidate && candidate.Span.Contains(span) ? candidate : null;
    }

    private static XamlSyntaxNode Shift(XamlSyntaxNode node, int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0) return node;
        return node switch
        {
            XamlElementSyntax element => element with
            {
                NameSpan = Offset(element.NameSpan, delta),
                OpenTagSpan = Offset(element.OpenTagSpan, delta),
                CloseTagSpan = Offset(element.CloseTagSpan, delta),
                EndNameSpan = Offset(element.EndNameSpan, delta),
                Attributes = element.Attributes.Select(a => a with { NameSpan = Offset(a.NameSpan, delta), ValueSpan = Offset(a.ValueSpan, delta), Span = Offset(a.Span, delta) }).ToImmutableArray(),
                Children = element.Children.Select(child => Shift(child, delta, cancellationToken)).ToImmutableArray(),
                FullSpan = Offset(element.Span, delta)
            },
            XamlTextSyntax text => text with { FullSpan = Offset(text.Span, delta) },
            XamlTriviaSyntax trivia => trivia with { FullSpan = Offset(trivia.Span, delta) },
            _ => throw new InvalidOperationException("Unknown source node kind.")
        };
    }

    private static TextSpan Offset(TextSpan span, int delta) => new(checked(span.Start + delta), span.Length);
    private static int Count(XamlSyntaxNode node) => node is XamlElementSyntax element ? 1 + element.Children.Sum(Count) : 1;
}
