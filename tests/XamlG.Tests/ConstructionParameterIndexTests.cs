using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ConstructionParameterIndexTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(256)]
    public void Substitution_preserves_identity_and_first_occurrence_at_every_size(int count)
    {
        var compilation = CompilationFactory.Create("namespace Model { public class Root { } }");
        var type = compilation.GetSpecialType(SpecialType.System_String);
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:Model'/>"), compilation);
        Assert.True(document.Success);
        var nodes = Enumerable.Range(0, count).Select(_ => document.Root! with { }).ToArray();
        var constants = Enumerable.Range(0, count).Select(_ => new BoundConstantExpression("same", type, new(0, 1))).ToArray();
        if (count > 1) { nodes[count - 1] = nodes[0]; constants[count - 1] = constants[0]; }
        var parameters = new ConstructionParameters(nodes, constants.Select(value => (value, (ITypeSymbol)type)).ToArray());
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < count; i++)
            {
                var first = count > 1 && i == count - 1 ? 0 : i;
                Assert.Equal("__key" + first, parameters.Key(nodes[i]));
                Assert.Equal("__sourceIndex" + first, parameters.SourceIndex(nodes[i]));
                Assert.Equal("__literal" + first, parameters.Constant(constants[i]));
            }
            Assert.Null(parameters.Key(document.Root! with { }));
            Assert.Null(parameters.SourceIndex(document.Root! with { }));
            Assert.Null(parameters.Constant(new("same", type, new(0, 1))));
        }
    }
}
