namespace XamlG.LanguageServer;

internal static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string Cancel = "$/cancelRequest";
    public const string Open = "textDocument/didOpen";
    public const string Change = "textDocument/didChange";
    public const string Close = "textDocument/didClose";
    public const string Hover = "textDocument/hover";
    public const string Completion = "textDocument/completion";
    public const string Definition = "textDocument/definition";
    public const string References = "textDocument/references";
    public const string Highlights = "textDocument/documentHighlight";
    public const string Symbols = "textDocument/documentSymbol";
    public const string Folding = "textDocument/foldingRange";
    public const string SemanticTokens = "textDocument/semanticTokens/full";
    public const string Diagnostics = "textDocument/publishDiagnostics";
    public const string Inspect = "xamlg/inspect";
}
