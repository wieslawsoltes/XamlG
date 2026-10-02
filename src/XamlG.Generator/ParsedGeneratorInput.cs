using XamlG.Syntax;

namespace XamlG.Generator;

internal sealed record ParsedGeneratorInput(GeneratorInput Input, XamlSyntaxTree Syntax)
{
    public string? ClassName => Syntax.Root is { } root ? NamespaceScope.Empty.Push(root).Directive(root, "Class")?.Value : null;
}
