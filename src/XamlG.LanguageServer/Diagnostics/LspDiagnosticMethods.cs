namespace XamlG.LanguageServer.Diagnostics;

internal static class LspDiagnosticMethods
{
    public const string Document = "textDocument/diagnostic";
    public const string Workspace = "workspace/diagnostic";
    public const string Refresh = "workspace/diagnostic/refresh";
    public const string Identifier = "xamlg";
    public const string Full = "full";
    public const string Unchanged = "unchanged";
    public static bool IsPull(string method) => method is Document or Workspace;
}
