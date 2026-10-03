namespace XamlG.Playground;

public sealed record PlaygroundDiagnostic(string Code, string Message, string Severity,
    string Path, int StartLine, int StartColumn, int EndLine, int EndColumn);
