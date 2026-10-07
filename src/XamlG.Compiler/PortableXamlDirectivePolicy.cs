using XamlG.Syntax;
using System.Threading;

namespace XamlG.Compiler;

internal sealed class PortableXamlDirectivePolicy : IXamlDirectivePolicy
{
    public bool ShouldCompile(XamlSyntaxTree syntax, XamlCompilerOptions options) => true;
    public IEnumerable<XamlDiagnostic> GetSkippedDiagnostics(XamlSyntaxTree syntax, CancellationToken cancellationToken) => Array.Empty<XamlDiagnostic>();
    public string BindClassModifier(BindingContext context, XamlAttributeSyntax? directive)
    {
        if (directive == null) return "public";
        if (directive.Value is "public" or "internal") return directive.Value;
        context.Report("XG1018", "x:ClassModifier must be public or internal.", directive.ValueSpan);
        return "public";
    }
    public string BindFieldModifier(BindingContext context, XamlAttributeSyntax? directive)
    {
        if (directive == null) return "internal";
        if (directive.Value is "public" or "internal" or "private" or "protected" or "protected internal" or "private protected") return directive.Value;
        context.Report("XG1018", "Invalid x:FieldModifier.", directive.ValueSpan);
        return "internal";
    }
}
