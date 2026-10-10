using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.CSharp.Integration;

/// <summary>Syntax-only candidates, never semantic models or project decisions. A weak
/// tree key and one replaceable method-name entry bound retention across custom profiles.</summary>
internal sealed record LoaderSyntaxCandidates(string MethodName,
    ImmutableArray<InvocationExpressionSyntax> Invocations, ImmutableArray<IdentifierNameSyntax> MethodGroups)
{
    private static readonly ConditionalWeakTable<SyntaxTree, Slot> Trees = new();
    private sealed class Slot { public LoaderSyntaxCandidates? Value; }

    public static LoaderSyntaxCandidates Get(SyntaxTree tree, string methodName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var slot = Trees.GetValue(tree, static _ => new());
        var cached = Volatile.Read(ref slot.Value);
        if (cached?.MethodName == methodName) return cached;
        var value = Scan(tree, methodName, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // Competing configurations may replace the single entry. Each caller gets
        // its own correct result, without an unbounded per-tree name dictionary.
        Volatile.Write(ref slot.Value, value);
        return value;
    }

    private static LoaderSyntaxCandidates Scan(SyntaxTree tree, string methodName, CancellationToken cancellation)
    {
        ImmutableArray<InvocationExpressionSyntax>.Builder? calls = null;
        ImmutableArray<IdentifierNameSyntax>.Builder? groups = null;
        // DescendantNodes uses Roslyn's iterative traversal, not a recursive walker.
        // Retain the old independent preorder of invocations and method groups.
        foreach (var node in tree.GetRoot(cancellation).DescendantNodes())
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is InvocationExpressionSyntax invocation)
            {
                var name = invocation.Expression is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText :
                    invocation.Expression is SimpleNameSyntax simple ? simple.Identifier.ValueText : string.Empty;
                if (name == methodName) (calls ??= ImmutableArray.CreateBuilder<InvocationExpressionSyntax>()).Add(invocation);
            }
            else if (node is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == methodName)
            {
                ExpressionSyntax expression = identifier.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Name, identifier) ? member : identifier;
                if (expression.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Expression, expression)) continue;
                if (identifier.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => i.Expression is IdentifierNameSyntax n && n.Identifier.ValueText == "nameof")) continue;
                (groups ??= ImmutableArray.CreateBuilder<IdentifierNameSyntax>()).Add(identifier);
            }
        }
        return new(methodName, calls?.ToImmutable() ?? ImmutableArray<InvocationExpressionSyntax>.Empty,
            groups?.ToImmutable() ?? ImmutableArray<IdentifierNameSyntax>.Empty);
    }
}
