using System.Collections.Immutable;
using System.Threading;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Framework references are resolved against the binder's real template namescope identities.</summary>
public sealed class XamlNameReferenceIndex
{
    private XamlNameReferenceIndex(ImmutableArray<XamlNameOccurrence> occurrences) => Occurrences = occurrences;
    public ImmutableArray<XamlNameOccurrence> Occurrences { get; }
    public XamlNameOccurrence? At(int position) => Occurrences.Where(o => o.Span.Contains(position)).OrderBy(o => o.Span.Length).FirstOrDefault();
    public XamlNameOccurrence? Declaration(XamlNameOccurrence occurrence) =>
        Occurrences.FirstOrDefault(o => o.IsDeclaration && o.Name == occurrence.Name && o.NameScopeId == occurrence.NameScopeId);
    public ImmutableArray<XamlNameOccurrence> References(XamlNameOccurrence occurrence) =>
        Occurrences.Where(o => o.Name == occurrence.Name && o.NameScopeId == occurrence.NameScopeId).ToImmutableArray();

    public static XamlNameReferenceIndex Create(XamlAnalysis analysis, XamlCompilationSession compiler, CancellationToken cancellationToken = default)
    {
        if (analysis == null) throw new ArgumentNullException(nameof(analysis));
        if (compiler == null) throw new ArgumentNullException(nameof(compiler));
        var result = ImmutableArray.CreateBuilder<XamlNameOccurrence>();
        var syntax = analysis.Syntax;
        if (syntax.HasErrors || syntax.Root == null) return new(result.ToImmutable());
        var objects = BoundDocumentTraversal.Objects(analysis.Document).GroupBy(o => o.Syntax.Span.Start).ToDictionary(g => g.Key, g => g.First());
        var pending = new Stack<(XamlElementSyntax Element, NamespaceScope Parent, int Scope)>();
        pending.Push((syntax.Root, NamespaceScope.Empty, analysis.Document.Root?.NameScopeId ?? 0));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, parent, nameScope) = pending.Pop();
            var scope = parent.Push(element);
            if (!XamlG.Compiler.References.XamlReferenceSyntax.IsRuntimeElement(element, scope, compiler.Types)) continue;
            if (objects.TryGetValue(element.Span.Start, out var obj)) nameScope = obj.NameScopeId;
            var declaration = scope.Directive(element, "Name");
            if (declaration != null && obj?.Name == declaration.Value)
                result.Add(new(declaration.Value, nameScope, declaration.ValueSpan, true, obj.Type));
            foreach (var rule in analysis.Document.Profile.NameReferenceRules)
                foreach (var reference in rule.GetReferences(syntax, element, scope, compiler.Types, cancellationToken))
                    result.Add(new(reference.Name, nameScope, reference.Span, false, null));
            foreach (var child in element.Children.OfType<XamlElementSyntax>().Reverse()) pending.Push((child, scope, nameScope));
        }
        return new(result.Distinct().OrderBy(o => o.Span.Start).ToImmutableArray());
    }
}
