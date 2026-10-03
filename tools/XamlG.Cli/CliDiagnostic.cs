namespace XamlG.Cli;

internal sealed record CliDiagnostic(string Code, string Message, string Severity, string Path, int Line, int Column);
