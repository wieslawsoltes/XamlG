using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;

namespace XamlG.Tooling;

/// <summary>A consistent syntax, semantic and output snapshot shared by all editor hosts.</summary>
public sealed record XamlAnalysis(XamlSyntaxTree Syntax, BoundDocument Document, XamlEmissionResult Output);
