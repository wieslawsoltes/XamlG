namespace XamlG.Syntax;
public sealed record XamlParseOptions(int MaximumDepth = 256, int MaximumCharacters = 4_194_304, int MaximumDiagnostics = 256);
