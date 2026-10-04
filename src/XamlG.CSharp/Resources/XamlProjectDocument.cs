using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Syntax retains the physical diagnostic path; LogicalPath determines reproducible generated identity and resource addressing.</summary>
public sealed record XamlProjectDocument(XamlSyntaxTree Syntax, string LogicalPath, string? ResourceUri = null);
