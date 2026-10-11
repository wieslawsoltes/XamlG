using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace XamlG.Compiler;

/// <summary>Reuses the exact legacy duplicate-assignment string identity. The
/// value holds text only; weak symbol keys cannot root a compilation or document.</summary>
internal static class AssignmentMemberName
{
    private static readonly ConditionalWeakTable<ISymbol, Name> Names = new();
    private sealed class Name(string text) { public string Text { get; } = text; }

    // Do not replace textual duplicate detection with symbol equality: distinct
    // symbols supplied by custom rules can intentionally have the same spelling.
    public static string Get(ISymbol symbol) => Names.GetValue(symbol,
        static value => new(value.ToDisplayString())).Text;
}
