using XamlG.Syntax;
namespace XamlG.CSharp;
public sealed record XamlSourceMapping(TextSpan GeneratedSpan, TextSpan SourceSpan, string SourcePath);
