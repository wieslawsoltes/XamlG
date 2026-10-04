using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
using XamlG.Tooling.Formatting;

namespace XamlG.Tooling.Refactoring;

/// <summary>Source-preserving tag rewrites, formatting and uniquely identified member-spelling fixes.</summary>
public sealed class XamlCodeActionService(XamlCompilationSession compiler)
{
    public ImmutableArray<XamlCodeAction> GetActions(XamlAnalysis analysis, TextSpan selection,
        XamlFormattingOptions? formatting = null, CancellationToken cancellationToken = default)
    {
        var syntax = analysis.Syntax;
        if (syntax.HasErrors || syntax.Root == null) return ImmutableArray<XamlCodeAction>.Empty;
        var result = ImmutableArray.CreateBuilder<XamlCodeAction>();
        var element = syntax.FindElement(selection.Start);
        if (element == null) return result.ToImmutable();
        if (element.IsSelfClosing)
            result.Add(new("Expand self-closing element", "refactor.rewrite", false,
                ImmutableArray.Create(new XamlTextChange(new(element.OpenTagSpan.End - 2, 2), "></" + element.Name + ">"))));
        else if (element.Children.Length == 0 && element.CloseTagSpan.Length > 0)
            result.Add(new("Collapse empty element", "refactor.rewrite", false,
                ImmutableArray.Create(new XamlTextChange(TextSpan.FromBounds(element.OpenTagSpan.End - 1, element.CloseTagSpan.End), " />"))));
        var formatted = XamlFormatter.Format(syntax, formatting, analysis: analysis, cancellationToken: cancellationToken);
        if (!formatted.IsEmpty) result.Add(new("Format XAML document", "source.format", false, formatted));

        var scope = NamespaceScope.Empty;
        foreach (var ancestor in syntax.Root.DescendantsAndSelf().Where(e => e.Span.Contains(element.Span)).OrderByDescending(e => e.Span.Length)) scope = scope.Push(ancestor);
        var expanded = scope.Expand(element.Name);
        var type = expanded.Namespace == null ? null : compiler.Types.Resolve(expanded.Namespace, expanded.LocalName).Type;
        if (type != null)
        {
            var names = type.Members().Where(m => !m.IsStatic && compiler.Types.IsAccessible(m) &&
                (m is IPropertySymbol { IsIndexer: false, SetMethod: { } setter } && compiler.Types.IsAccessible(setter) ||
                 m is IEventSymbol { AddMethod: { } add } && compiler.Types.IsAccessible(add)))
                .Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var attribute in element.Attributes.Where(a => !a.IsNamespace && !a.Name.Contains(':') && !a.Name.Contains('.') &&
                (a.Span.Contains(selection.Start) || selection.Contains(a.Span)) && !names.Contains(a.Name)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = names.Where(n => DistanceOne(attribute.Name, n) && !element.Attributes.Any(a => a.Name == n)).ToArray();
                if (candidates.Length != 1) continue;
                var edit = new XamlTextChange(attribute.NameSpan, candidates[0]);
                result.Add(new("Change '" + attribute.Name + "' to '" + candidates[0] + "'", "quickfix", true, ImmutableArray.Create(edit)));
            }
        }
        return result.ToImmutable();
    }
    private static bool DistanceOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        if (left.Length == right.Length)
        {
            var differences = Enumerable.Range(0, left.Length).Where(i => left[i] != right[i]).ToArray();
            return differences.Length == 1 || differences.Length == 2 && differences[1] == differences[0] + 1 &&
                left[differences[0]] == right[differences[1]] && left[differences[1]] == right[differences[0]];
        }
        var shorter = left.Length < right.Length ? left : right; var longer = left.Length < right.Length ? right : left;
        var index = 0;
        while (index < shorter.Length && shorter[index] == longer[index]) index++;
        return shorter.Substring(index) == longer.Substring(index + 1);
    }
}
