using System.Threading;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler.References;

/// <summary>Supplies exact reference syntax for semantic editor operations. It never executes application code.</summary>
public interface IXamlNameReferenceRule
{
    IEnumerable<XamlNameReference> GetReferences(XamlSyntaxTree tree, XamlElementSyntax element,
        NamespaceScope scope, RoslynTypeSystem types, CancellationToken cancellationToken);
}
