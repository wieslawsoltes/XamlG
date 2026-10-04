using XamlG.Syntax;
namespace XamlG.Tooling.Navigation;
public sealed record XamlResourceReference(TextSpan Span, string ResourceUri, string? TargetPath, string RootType, bool IsExternal);
