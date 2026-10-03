namespace XamlG.LanguageServer;

public sealed record LspTextChange(LspRange? Range, string Text);
