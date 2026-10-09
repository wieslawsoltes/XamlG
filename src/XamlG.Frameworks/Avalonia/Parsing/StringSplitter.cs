using System;

namespace XamlG.Frameworks.Avalonia.Parsing;

/// <summary>
/// Helpers for splitting strings.
/// </summary>
internal static class StringSplitter
{
    private const char DefaultOpeningParenthesis = '(';
    private const char DefaultClosingParenthesis = ')';

    /// <summary>
    /// Splits the provided string by the specified separators, but ignores separators that
    /// appear inside matching bracket pairs (<paramref name="openingBracket"/> / <paramref name="closingBracket"/>).
    /// </summary>
    /// <param name="s">The input string to split. If <c>null</c>, an empty array is returned.</param>
    /// <param name="separator">The separator character to split on.</param>
    /// <param name="openingBracket">The character that opens a bracketed section. <c>(</c> by default.</param>
    /// <param name="closingBracket">The character that closes a bracketed section. <c>)</c> by default.</param>
    /// <param name="options">Options for trimming entries and removing empty entries.</param>
    /// <returns>An array of split segments. Returns an empty array if the input is null or only whitespace.</returns>
    public static string[] SplitRespectingBrackets(string? s, char separator,
        char openingBracket = DefaultOpeningParenthesis, char closingBracket = DefaultClosingParenthesis,
        StringSplitOptions options = StringSplitOptions.None) =>
        SplitRespectingBrackets(s, [separator], openingBracket, closingBracket, options);

    /// <summary>
    /// Splits the provided string by the specified separator, but ignores separators that
    /// appear inside matching bracket pairs (<paramref name="openingBracket"/> / <paramref name="closingBracket"/>).
    /// </summary>
    /// <param name="s">The input string to split. If <c>null</c>, an empty array is returned.</param>
    /// <param name="separators">The separator characters to split on.</param>
    /// <param name="openingBracket">The character that opens a bracketed section. <c>(</c> by default.</param>
    /// <param name="closingBracket">The character that closes a bracketed section. <c>)</c> by default.</param>
    /// <param name="options">Options for trimming entries and removing empty entries.</param>
    /// <returns>An array of split segments. Returns an empty array if the input is null or only whitespace.</returns>
    public static string[] SplitRespectingBrackets(string? s, ReadOnlySpan<char> separators,
        char openingBracket = DefaultOpeningParenthesis, char closingBracket = DefaultClosingParenthesis,
        StringSplitOptions options = StringSplitOptions.None)
    {
        if (openingBracket == closingBracket)
            throw new ArgumentException($"Opening bracket and closing bracket cannot be the same character '{openingBracket}'.", nameof(closingBracket));

        if (s is null)
            return [];

        var parts = new BracketSplitEnumerator(s.AsSpan(), separators, openingBracket, closingBracket, options);
        var result = new string[parts.Count];
        var index = 0;
        foreach (var part in parts) result[index++] = part.ToString();
        return result;
    }
}
