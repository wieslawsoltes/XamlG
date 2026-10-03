using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
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
}
