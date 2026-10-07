using XamlG.Syntax;
using System.Threading;

namespace XamlG.Compiler;

/// <summary>Framework rules for document opt-outs and generated class/field visibility.</summary>
public interface IXamlDirectivePolicy
{
    bool ShouldCompile(XamlSyntaxTree syntax, XamlCompilerOptions options);
    IEnumerable<XamlDiagnostic> GetSkippedDiagnostics(XamlSyntaxTree syntax, CancellationToken cancellationToken);
    string BindClassModifier(BindingContext context, XamlAttributeSyntax? directive);
    string BindFieldModifier(BindingContext context, XamlAttributeSyntax? directive);
}
