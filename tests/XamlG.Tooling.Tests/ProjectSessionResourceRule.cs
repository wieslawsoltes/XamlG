using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Syntax;

namespace XamlG.Tooling.Tests;

internal sealed class ProjectSessionResourceRule : IXamlObjectExpressionRule
{
    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType, NamespaceScope parentScope,
        int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.Name != "Include") return false;
        var source = syntax.Attributes.Single(a => a.Name == "Source");
        var result = context.Options.Resources!.Resolve(context.Options.ResourceUri, source.Value);
        if (!result.Success) context.Report("TEST0001", result.Error!, source.ValueSpan);
        else expression = new BoundResourceExpression(result.Resource!, source.ValueSpan);
        return true;
    }
}
