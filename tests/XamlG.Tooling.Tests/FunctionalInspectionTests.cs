using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class FunctionalInspectionTests
{
    [Fact]
    public void DelegateInspectionContainsParameterAndPropertyBody()
    {
        var session = ToolingFixture.Create();
        var compilation = session.Types.Compilation;
        var type = compilation.GetTypeByMetadataName("Model.View")!;
        var property = type.GetMembers("Text").OfType<IPropertySymbol>().Single();
        var parameter = new BoundParameterExpression("view", type, new(0, 1));
        var body = new BoundPropertyAccessExpression(parameter, property, ImmutableArray<BoundExpression>.Empty, new(0, 1));
        var delegateType = compilation.GetTypeByMetadataName("System.Func`2")!.Construct(type, property.Type);
        var lambda = new BoundLambdaExpression(delegateType, ImmutableArray.Create(parameter), body, true, new(0, 1));
        var tree = XamlInspector.Expression(lambda);
        Assert.Equal("static (view) =>", tree.Label);
        Assert.Equal(2, tree.Children.Length);
        Assert.Equal(nameof(BoundParameterExpression), tree.Children[0].Kind);
        Assert.Equal(nameof(BoundPropertyAccessExpression), tree.Children[1].Kind);
        Assert.Contains("Text", tree.Children[1].Label);
        Assert.Single(tree.Children[1].Children);
    }

    [Fact]
    public void EventExpressionsParticipateInInspectionAndDocumentTraversal()
    {
        var compilation = ToolingFixture.Create().Types.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            namespace Model
            {
                public class Button { public event System.EventHandler Click; }
                public class HandlerExtension { public System.EventHandler ProvideValue() => (_, _) => { }; }
            }
            """));
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Button xmlns='clr-namespace:Model' Click='{Handler}'/>"), compilation);
        Assert.True(document.Success, string.Join(Environment.NewLine, document.Diagnostics));
        Assert.Contains(BoundDocumentTraversal.Objects(document), value => value.Type.Name == "HandlerExtension");
        Assert.IsType<BoundMarkupExpression>(Assert.Single(BoundDocumentTraversal.Expressions(document)));
        var assignment = Assert.Single(XamlInspector.Bound(document)!.Children);
        Assert.Equal(nameof(BoundEventAssignment), assignment.Kind);
        Assert.Equal("MarkupExtension", Assert.Single(assignment.Children).Kind);
    }
}
