namespace XamlG.LanguageServer.Diagnostics;

/// <summary>Value-semantic wire diagnostic shared by push and pull projection.</summary>
public sealed record LspDiagnosticItem(LspRange Range, int Severity, string Code, string Source, string Message);
