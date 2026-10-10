using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

internal sealed class EmissionAnalysisFixture
{
    public BoundDocument Document { get; }
    public BoundObject Root { get; }
    public BoundMember Member { get; }
    public IArrayTypeSymbol ArrayType { get; }

    public EmissionAnalysisFixture()
    {
        var compilation = CompilationFactory.Create("namespace Traversal { public class Node { public Node Child { get; set; } } }");
        Document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Traversal'/>"), compilation);
        Assert.True(Document.Success, string.Join("\n", Document.Diagnostics));
        Root = Document.Root!;
        var property = (IPropertySymbol)Root.Type.GetMembers("Child").Single();
        Member = new(property.Name, BoundMemberKind.Property, property, property.Type,
            property.GetMethod, property.SetMethod, default);
        ArrayType = compilation.CreateArrayTypeSymbol(Root.Type);
    }
}
