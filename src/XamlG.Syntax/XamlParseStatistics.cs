namespace XamlG.Syntax;

/// <summary>Per-snapshot frontend work counters; reused nodes are the exact immutable objects from the preceding revision.</summary>
public sealed record XamlParseStatistics(int ParsedCharacters, int ReusedNodes, bool IsIncremental);
