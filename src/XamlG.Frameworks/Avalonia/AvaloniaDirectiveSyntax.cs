using System.Threading;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

internal static class AvaloniaDirectiveSyntax
{
    public static IEnumerable<XamlDiagnostic> ValidateSkipped(XamlSyntaxTree syntax, CancellationToken cancellationToken)
    {
        var diagnostics = new List<XamlDiagnostic>();
        if (syntax.Root == null) return diagnostics;
        var pending = new Stack<(XamlElementSyntax Element, NamespaceScope Scope)>();
        pending.Push((syntax.Root, NamespaceScope.Empty));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, parent) = pending.Pop();
            var scope = parent.Push(element);
            ValidatePrefix(element.Name, element.NameSpan, scope, diagnostics.Add);
            foreach (var attribute in element.Attributes.Where(attribute => !attribute.IsNamespace))
            {
                ValidatePrefix(attribute.Name, attribute.NameSpan, scope, diagnostics.Add);
                var expanded = scope.Expand(attribute.Name, true);
                if (expanded.Namespace == XamlNames.Xml) continue;
                if (expanded is { Namespace: XamlNames.Language2006, LocalName: "TypeArguments" })
                {
                    XamlTypeNameParser.ParseList(attribute.Value, attribute.ValueSpan, diagnostics.Add);
                }
                else ValidateMarkup(attribute.Value, attribute.ValueSpan, scope, diagnostics.Add, cancellationToken);
            }
            foreach (var child in element.Children.OfType<XamlElementSyntax>().Reverse()) pending.Push((child, scope));
        }
        return diagnostics;
    }

    public static void ValidateMarkup(string text, TextSpan span, NamespaceScope scope, Action<XamlDiagnostic> report, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Text, TextSpan Span)>();
        pending.Push((text, span));
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = pending.Pop();
            if (MarkupExtensionParser.Parse(value.Text, value.Span, report) is not { } markup) continue;
            foreach (var argument in markup.Arguments)
            {
                var argumentSpan = argument.ValueSpan ?? argument.Span;
                if (argument.Name != null && scope.Expand(argument.Name, true) is { Namespace: XamlNames.Language2006, LocalName: "TypeArguments" })
                    XamlTypeNameParser.ParseList(argument.Value, argumentSpan, report);
                else pending.Push((argument.Value, argumentSpan));
            }
        }
    }

    private static void ValidatePrefix(string name, TextSpan span, NamespaceScope scope, Action<XamlDiagnostic> report)
    {
        if (scope.Expand(name).Namespace == null) report(new("XG1001", $"Namespace prefix for '{name}' is not declared.", span));
    }
}
