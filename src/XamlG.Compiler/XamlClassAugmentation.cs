using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XamlG.Compiler;

/// <summary>Whether generated members can legally share the source component declaration.
/// Non-partial declarations instead use a separate typed factory; application syntax is never rewritten.</summary>
public static class XamlClassAugmentation
{
    public static bool IsAvailable(INamedTypeSymbol type, CancellationToken cancellationToken = default)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        for (var current = type; current != null; current = current.ContainingType)
        {
            if (current.DeclaringSyntaxReferences.IsEmpty) return false;
            foreach (var reference in current.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration ||
                    !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)) return false;
            }
        }
        return true;
    }
}
