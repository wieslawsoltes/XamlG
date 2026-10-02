namespace XamlG.Syntax;
public sealed record XamlDiagnostic(string Code, string Message, TextSpan Span, XamlSeverity Severity = XamlSeverity.Error);
