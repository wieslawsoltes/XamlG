using XamlG.LanguageServer.Diagnostics;

namespace XamlG.LanguageServer;

internal static class LspServerCapabilities
{
    public static object Create(LspClientFeatures client)
    {
        var result = new Dictionary<string, object>
        {
            ["positionEncoding"] = "utf-16",
            ["textDocumentSync"] = new { openClose = true, change = 2 },
            ["hoverProvider"] = true,
            ["completionProvider"] = new { resolveProvider = false, triggerCharacters = new[] { "<", ":", " ", "=", "{" } },
            ["definitionProvider"] = true, ["referencesProvider"] = true, ["documentHighlightProvider"] = true,
            ["documentSymbolProvider"] = true, ["foldingRangeProvider"] = true,
            ["renameProvider"] = new { prepareProvider = true },
            ["documentFormattingProvider"] = true, ["documentRangeFormattingProvider"] = true,
            ["codeActionProvider"] = new { codeActionKinds = new[] { "quickfix", "refactor.rewrite", "source.format" }, resolveProvider = false },
            ["documentLinkProvider"] = new { resolveProvider = false }, ["workspaceSymbolProvider"] = true,
            ["semanticTokensProvider"] = new { legend = new { tokenTypes = LspSemanticTokens.Legend, tokenModifiers = Array.Empty<string>() }, full = new { delta = true }, range = true }
        };
        if (client.PullDiagnostics)
            result["diagnosticProvider"] = new { identifier = LspDiagnosticMethods.Identifier, interFileDependencies = true, workspaceDiagnostics = true };
        if (client.WillRenameFiles)
            result["workspace"] = new { fileOperations = new { willRename = new
            {
                filters = new object[]
                {
                    new { scheme = "file", pattern = new { glob = "**/*.{xaml,axaml}", matches = "file", options = new { ignoreCase = true } } },
                    new { scheme = "file", pattern = new { glob = "**", matches = "folder" } }
                }
            } } };
        return result;
    }
}
