using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Syntax;

namespace XamlG.Tooling.Tests;

internal sealed class FileMoveResourceRule : IXamlObjectExpressionRule
{
    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (syntax.LocalName != "Include") return false;
        var attribute = syntax.Attributes.SingleOrDefault(a => a.Name == "Source");
        var property = syntax.Children.OfType<XamlElementSyntax>().SingleOrDefault(e => e.LocalName == "Include.Source");
        var text = attribute?.Value ?? string.Concat(property!.Children.OfType<XamlTextSyntax>().Select(t => t.Value)).Trim();
        var span = attribute?.ValueSpan ?? property!.Span;
        var resolved = context.Options.Resources!.Resolve(context.Options.ResourceUri, text);
        if (!resolved.Success) context.Report("TESTFILE", resolved.Error!, span);
        else expression = new BoundResourceExpression(resolved.Resource!, span);
        return true;
    }
}
